using System;
using System.IO;
using QingReminder.Core;

internal static class CoreTests
{
    private static int passed;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(8));

    private static int Main()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), "QingReminderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            Run("Add, persist and trigger exactly once", delegate { AddAndPoll(Path.Combine(testDirectory, "basic.json")); });
            Run("Manual countdown input carries minutes and seconds and rejects invalid durations", CountdownInputValidation);
            Run("Idle polling reuses its empty result and tracks the next scheduled reminder", delegate { IdlePolling(Path.Combine(testDirectory, "idle.json")); });
            Run("Missed and ringing reminders survive restart", delegate { Restart(Path.Combine(testDirectory, "restart.json")); });
            Run("Postpone, edit and complete retain identity", delegate { Reschedule(Path.Combine(testDirectory, "edit.json")); });
            Run("48-minute countdown survives restart, snooze and repeated rescheduling", delegate { CountdownRepeat(Path.Combine(testDirectory, "repeat.json")); });
            Run("Countdown duration belongs to each alarm and follows explicit edits", delegate { CountdownEdits(Path.Combine(testDirectory, "durations.json")); });
            Run("Legacy countdown storage recovers its original duration", delegate { LegacyCountdown(Path.Combine(testDirectory, "legacy.json")); });
            Run("Invalid persisted countdown duration is preserved as corrupt data", delegate { InvalidCountdownDuration(Path.Combine(testDirectory, "bad-duration.json")); });
            Run("Invalid schedules never change persisted state", delegate { Validation(Path.Combine(testDirectory, "validation.json")); });
            Run("Malformed storage is backed up and logged", delegate { Malformed(Path.Combine(testDirectory, "malformed.json")); });
            Run("Recovery logs rotate while preserving corrupt alarm backups", delegate { RecoveryLogRotation(Path.Combine(testDirectory, "recovery-log.json")); });
            Run("Unsupported newer data is preserved", delegate { FutureVersion(Path.Combine(testDirectory, "future.json")); });
            Run("Failed persistence leaves in-memory state intact", delegate { FailedSave(Path.Combine(testDirectory, "failure.json")); });
            Console.WriteLine("Passed " + passed + " core tests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.Delete(testDirectory, true);
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void AddAndPoll(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        int changes = 0;
        scheduler.Changed += delegate { changes++; };
        AlarmEntry first = scheduler.AddAbsolute("meeting", Now.AddMinutes(1), Now);
        AlarmEntry second = scheduler.AddCountdown("  break  ", TimeSpan.FromMinutes(2), Now);
        Assert(first.Id != second.Id, "Identifiers must be unique.");
        Assert(second.Title == "break", "Titles should be trimmed.");
        Assert(!first.CountdownDuration.HasValue, "An absolute alarm should not retain a countdown interval.");
        Assert(second.CountdownDuration == TimeSpan.FromMinutes(2), "Countdown creation should retain the complete interval.");
        Assert(scheduler.Poll(Now.AddSeconds(59)).Count == 0, "No reminder should ring early.");
        Assert(scheduler.Poll(Now.AddMinutes(1)).Count == 1, "First reminder should ring at its due time.");
        Assert(scheduler.Poll(Now.AddMinutes(1)).Count == 0, "Repeated polling must not duplicate ringing.");
        Assert(scheduler.Poll(Now.AddMinutes(2)).Count == 1, "Second reminder should ring.");
        Assert(changes == 4, "Change events should match persisted changes only.");
        Assert(File.Exists(path + ".bak"), "Replacing the JSON should retain the previous valid file.");
        Assert(new AlarmStore(path).Load()[0].State == AlarmState.Ringing, "Ringing state should be durable.");
        Assert(first.State == AlarmState.Scheduled, "Returned entries must be detached snapshots.");
        Expect<NotSupportedException>(delegate { scheduler.Alarms.Clear(); });
    }

    private static void CountdownInputValidation()
    {
        Assert(CountdownInput.Parse("00", "48", "00") == TimeSpan.FromMinutes(48), "Original minutes must parse exactly.");
        Assert(CountdownInput.Parse("00", "70", "00") == new TimeSpan(1, 10, 0), "Minutes above 59 must carry into hours.");
        Assert(CountdownInput.Parse("1", "70", "70") == new TimeSpan(2, 11, 10), "All manually entered units must add together.");
        Assert(CountdownInput.Parse("00", "00", "70") == new TimeSpan(0, 1, 10), "Seconds above 59 must carry into minutes.");
        Assert(CountdownInput.Parse("25", "00", "01") == TimeSpan.FromHours(25).Add(TimeSpan.FromSeconds(1)), "Hours must not wrap at one day.");
        Assert(CountdownInput.Parse("0", "525600", "0") == AlarmScheduler.MaxDelay, "The existing duration boundary must remain valid.");
        Expect<ArgumentException>(delegate { CountdownInput.Parse("0", "0", "0"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("0", "-1", "0"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("0", "0", "1.5"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("", "48", "0"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("0", "525600", "1"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("9223372036854775807", "9223372036854775807", "9223372036854775807"); });
        Expect<ArgumentException>(delegate { CountdownInput.Parse("0", "0", "999999999999999999999999999999"); });
    }

    private static void Restart(string path)
    {
        AlarmScheduler firstSession = new AlarmScheduler(new AlarmStore(path));
        firstSession.AddAbsolute("missed", Now.AddMinutes(1), Now);
        firstSession.AddCountdown("unacknowledged", TimeSpan.FromMinutes(2), Now);
        firstSession.Poll(Now.AddMinutes(1));
        AlarmScheduler secondSession = new AlarmScheduler(new AlarmStore(path));
        Assert(secondSession.Poll(Now.AddHours(1)).Count == 2, "Restart must recover both missed and unacknowledged reminders.");
        Assert(secondSession.Poll(Now.AddHours(1)).Count == 0, "Recovered reminders must still ring only once per session.");
    }

    private static void IdlePolling(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        Assert(scheduler.Count == 0 && !scheduler.NextScheduledAt.HasValue, "An empty scheduler should have no next deadline.");
        System.Collections.Generic.IList<AlarmEntry> empty = scheduler.Poll(Now);
        Expect<NotSupportedException>(delegate { empty.Add(new AlarmEntry()); });
        AlarmEntry later = scheduler.AddCountdown("later", TimeSpan.FromHours(2), Now);
        AlarmEntry sooner = scheduler.AddCountdown("sooner", TimeSpan.FromHours(1), Now);
        Assert(scheduler.Count == 2 && scheduler.NextScheduledAt == sooner.DueAt, "Adding reminders must update the earliest scheduled deadline.");
        string persisted = File.ReadAllText(path);
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (int index = 0; index < 100000; index++)
                Assert(Object.ReferenceEquals(empty, scheduler.Poll(Now.AddMinutes(30))), "Idle polls should reuse the same immutable empty result.");
        }
        Assert(File.ReadAllText(path) == persisted, "Idle polls must not write persisted reminders.");
        Assert(!scheduler.Remove("missing") && scheduler.NextScheduledAt == sooner.DueAt, "Removing an absent reminder must not alter the cached deadline.");
        scheduler.Remove(sooner.Id);
        Assert(scheduler.Count == 1 && scheduler.NextScheduledAt == later.DueAt, "Removing the first reminder must reveal the next deadline.");
        scheduler.Reschedule(later.Id, Now.AddMinutes(5), AlarmMode.Countdown, Now);
        Assert(scheduler.NextScheduledAt == Now.AddMinutes(5), "An edit must update the cached deadline.");
        System.Collections.Generic.IList<AlarmEntry> ringing = scheduler.Poll(Now.AddMinutes(5));
        Assert(ringing.Count == 1 && !scheduler.NextScheduledAt.HasValue, "Ringing reminders must not remain in the scheduled deadline cache.");
        ringing[0].Title = "changed snapshot";
        Assert(scheduler.Alarms[0].Title == "later", "A returned ringing reminder must remain a detached snapshot.");
        Assert(Object.ReferenceEquals(empty, scheduler.Poll(Now.AddHours(3))), "Already ringing reminders should keep idle polling allocation-free.");
        scheduler.Postpone(later.Id, TimeSpan.FromMinutes(48), Now.AddMinutes(5));
        Assert(scheduler.NextScheduledAt == Now.AddMinutes(53), "Postponing must restore the scheduled deadline.");
        scheduler.Complete(later.Id);
        Assert(scheduler.Count == 0 && !scheduler.NextScheduledAt.HasValue, "Completing the last reminder must clear the deadline cache.");
    }

    private static void Reschedule(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry original = scheduler.AddCountdown("tea", TimeSpan.FromMinutes(1), Now);
        scheduler.Poll(Now.AddMinutes(1));
        AlarmEntry postponed = scheduler.Postpone(original.Id, TimeSpan.FromMinutes(5), Now.AddMinutes(1));
        Assert(postponed.Id == original.Id && postponed.CreatedAt == original.CreatedAt, "Postponing must preserve identity.");
        Assert(postponed.State == AlarmState.Scheduled, "Postponing must clear ringing.");
        Assert(postponed.CountdownDuration == TimeSpan.FromMinutes(1), "A temporary postpone must retain the original countdown interval.");
        Assert(scheduler.Poll(Now.AddMinutes(5)).Count == 0, "A postponed reminder must wait for its new due time.");
        Assert(scheduler.Poll(Now.AddMinutes(6)).Count == 1, "A postponed reminder must ring at its new due time.");
        AlarmEntry edited = scheduler.Reschedule(original.Id, "new title", Now.AddHours(1), AlarmMode.Absolute, Now.AddMinutes(6));
        Assert(edited.Title == "new title" && edited.Mode == AlarmMode.Absolute, "Editing must update title and mode.");
        Assert(!edited.CountdownDuration.HasValue, "Editing to absolute time should clear the previous countdown interval.");
        Assert(new AlarmScheduler(new AlarmStore(path)).Alarms[0].DueAt == edited.DueAt, "New due time must be persisted.");
        Assert(scheduler.Complete(original.Id), "Completing an existing reminder should succeed.");
        Assert(!scheduler.Remove(original.Id), "Removing an absent reminder should be harmless.");
        Assert(new AlarmStore(path).Load().Count == 0, "Completed reminders must be removed durably.");
    }

    private static void CountdownRepeat(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry original = scheduler.AddCountdown("48-minute break", TimeSpan.FromMinutes(48), Now);
        Assert(original.CountdownDuration == TimeSpan.FromMinutes(48), "A 48-minute preset must store all 48 minutes.");
        AlarmScheduler restarted = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry reminder = restarted.Poll(Now.AddMinutes(48))[0];
        Assert(reminder.CountdownDuration == TimeSpan.FromMinutes(48), "Polling after a restart should provide the original duration.");
        AlarmEntry postponed = restarted.Postpone(reminder.Id, TimeSpan.FromMinutes(5), Now.AddMinutes(48));
        Assert(postponed.DueAt == Now.AddMinutes(53), "A temporary five-minute snooze should adjust only the next due time.");
        Assert(postponed.CountdownDuration == TimeSpan.FromMinutes(48), "Snoozing should preserve the original 48-minute interval.");
        restarted = new AlarmScheduler(new AlarmStore(path));
        reminder = restarted.Poll(Now.AddMinutes(53))[0];
        DateTimeOffset repeatedAt = Now.AddMinutes(53);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            TimeSpan savedDuration = reminder.CountdownDuration.Value;
            reminder = restarted.Reschedule(reminder.Id, repeatedAt.Add(savedDuration), AlarmMode.Countdown, repeatedAt);
            Assert(reminder.DueAt == repeatedAt.AddMinutes(48), "Each repeated reminder should wait a complete 48 minutes.");
            Assert(reminder.CountdownDuration == TimeSpan.FromMinutes(48), "Repeated rescheduling must not accumulate elapsed time.");
            Assert(restarted.Poll(reminder.DueAt.AddSeconds(-1)).Count == 0, "Repeating a countdown must not ring early.");
            repeatedAt = reminder.DueAt;
            restarted = new AlarmScheduler(new AlarmStore(path));
            reminder = restarted.Poll(repeatedAt)[0];
        }
        Assert(original.State == AlarmState.Scheduled && original.CountdownDuration == TimeSpan.FromMinutes(48), "Original returned snapshots must remain unchanged.");
    }

    private static void CountdownEdits(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry first = scheduler.AddCountdown("48 minutes", TimeSpan.FromMinutes(48), Now);
        AlarmEntry second = scheduler.AddCountdown("15 minutes", TimeSpan.FromMinutes(15), Now);
        AlarmEntry absolute = scheduler.AddAbsolute("appointment", Now.AddHours(1), Now);
        AlarmEntry edited = scheduler.Reschedule(first.Id, "20 minutes", Now.AddMinutes(30), AlarmMode.Countdown, Now.AddMinutes(10));
        Assert(edited.CountdownDuration == TimeSpan.FromMinutes(20), "An explicit countdown edit must replace the saved interval with the full new delay.");
        Assert(new AlarmStore(path).Load()[0].CountdownDuration == TimeSpan.FromMinutes(20), "Explicitly edited duration must persist independently from CreatedAt.");
        Assert(second.CountdownDuration == TimeSpan.FromMinutes(15), "Editing an alarm must not change another alarm's duration.");
        Assert(!absolute.CountdownDuration.HasValue, "Absolute alarms must not inherit another alarm's countdown.");
        AlarmEntry absoluteSnooze = scheduler.Postpone(absolute.Id, TimeSpan.FromMinutes(10), Now);
        Assert(absoluteSnooze.CountdownDuration == TimeSpan.FromMinutes(10), "An absolute alarm first postponed by a delay should remember that delay.");
        AlarmEntry toAbsolute = scheduler.Reschedule(first.Id, Now.AddHours(2), AlarmMode.Absolute, Now);
        Assert(!toAbsolute.CountdownDuration.HasValue, "Changing to absolute time must clear the countdown duration.");
        AlarmEntry toCountdown = scheduler.Reschedule(first.Id, Now.AddMinutes(48), AlarmMode.Countdown, Now);
        Assert(toCountdown.CountdownDuration == TimeSpan.FromMinutes(48), "Changing back to countdown must retain the newly selected interval.");
    }

    private static void LegacyCountdown(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry original = scheduler.AddCountdown("legacy 48 minutes", TimeSpan.FromMinutes(48), Now);
        string persisted = File.ReadAllText(path);
        string durationField = ",\"CountdownTicks\":" + TimeSpan.FromMinutes(48).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert(persisted.Contains(durationField), "The additive duration field should be present in new documents.");
        string legacy = persisted.Replace(durationField, String.Empty);
        File.WriteAllText(path, legacy);
        AlarmStore store = new AlarmStore(path);
        AlarmEntry loaded = store.Load()[0];
        Assert(loaded.Id == original.Id && loaded.CountdownDuration == TimeSpan.FromMinutes(48), "A valid legacy alarm should recover 48 minutes without changing its identity.");
        Assert(store.LastRecoveryMessage == null && File.ReadAllText(path) == legacy, "Valid legacy data must not be treated as corruption or altered by reading.");
        AlarmScheduler restarted = new AlarmScheduler(store);
        AlarmEntry ringing = restarted.Poll(Now.AddMinutes(48))[0];
        AlarmEntry repeated = restarted.Reschedule(ringing.Id, Now.AddMinutes(48).Add(ringing.CountdownDuration.Value), AlarmMode.Countdown, Now.AddMinutes(48));
        Assert(repeated.DueAt == Now.AddMinutes(96), "A recovered legacy alarm should repeat after its original 48 minutes.");
        Assert(File.ReadAllText(path).Contains(durationField), "The recovered duration should be written on the next normal mutation.");

        string unknownPath = path + ".unknown";
        AlarmStore unknownStore = new AlarmStore(unknownPath);
        unknownStore.Save(new[] { new AlarmEntry { Id = "unknown", Title = "legacy unknown", CreatedAt = Now, DueAt = Now.AddDays(366), Mode = AlarmMode.Countdown, State = AlarmState.Scheduled } });
        Assert(!unknownStore.Load()[0].CountdownDuration.HasValue, "An implausible legacy interval should remain unknown instead of corrupting valid old data.");
        Assert(unknownStore.LastRecoveryMessage == null, "Missing legacy interval is a recoverable migration condition.");
    }

    private static void InvalidCountdownDuration(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        scheduler.AddCountdown("invalid interval", TimeSpan.FromMinutes(48), Now);
        string durationField = "\"CountdownTicks\":" + TimeSpan.FromMinutes(48).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string invalid = File.ReadAllText(path).Replace(durationField, "\"CountdownTicks\":0");
        File.WriteAllText(path, invalid);
        AlarmStore store = new AlarmStore(path);
        Assert(store.Load().Count == 0 && File.Exists(store.RecoveryBackupPath), "An explicitly invalid interval should follow data recovery rules.");
        Assert(File.ReadAllText(store.RecoveryBackupPath) == invalid, "Invalid countdown data should be preserved for recovery.");
    }

    private static void Validation(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry entry = scheduler.AddCountdown(" ", TimeSpan.FromSeconds(1), Now);
        Assert(entry.Title.Length > 0, "An empty title should receive a default.");
        string before = File.ReadAllText(path);
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.AddCountdown("bad", TimeSpan.Zero, Now); });
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.AddCountdown("bad", TimeSpan.FromSeconds(-1), Now); });
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.AddCountdown("bad", TimeSpan.FromDays(366), Now); });
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.AddAbsolute("bad", Now, Now); });
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.Reschedule(entry.Id, Now.AddMinutes(2), (AlarmMode)9, Now); });
        Expect<ArgumentException>(delegate { scheduler.AddAbsolute(new String('x', 201), Now.AddMinutes(1), Now); });
        Expect<ArgumentOutOfRangeException>(delegate { scheduler.Postpone(entry.Id, TimeSpan.FromSeconds(1), DateTimeOffset.MaxValue); });
        Assert(scheduler.Alarms.Count == 1 && File.ReadAllText(path) == before, "Invalid input must not mutate memory or disk.");
        AlarmEntry offsetReminder = scheduler.AddAbsolute("offset", Now.ToOffset(TimeSpan.Zero).AddMinutes(1), Now);
        Assert(scheduler.Poll(Now.AddMinutes(1)).Count == 2, "Offsets should compare by the represented instant.");
        Assert(offsetReminder.DueAt.Offset == TimeSpan.Zero, "Persistence should preserve chosen offsets.");
    }

    private static void Malformed(string path)
    {
        const string broken = "{ not valid json";
        File.WriteAllText(path, broken);
        AlarmStore store = new AlarmStore(path);
        Assert(store.Load().Count == 0, "Malformed storage should recover to an empty list.");
        Assert(!String.IsNullOrWhiteSpace(store.LastRecoveryMessage), "Recovery must be surfaced to the caller.");
        Assert(File.Exists(store.RecoveryBackupPath), "Malformed storage must be preserved.");
        Assert(File.ReadAllText(store.RecoveryBackupPath) == broken, "Recovery must not alter the original malformed content.");
        Assert(File.Exists(path + ".log"), "Recovery should be logged.");
        new AlarmScheduler(store).AddAbsolute("new", Now.AddMinutes(1), Now);
        Assert(new AlarmStore(path).Load().Count == 1, "A recovered store should accept new reminders.");
    }

    private static void FutureVersion(string path)
    {
        const string document = "{\"Version\":2,\"Alarms\":[]}";
        File.WriteAllText(path, document);
        AlarmStore store = new AlarmStore(path);
        Expect<NotSupportedException>(delegate { store.Load(); });
        Assert(File.ReadAllText(path) == document, "Newer data formats must not be replaced or archived as corruption.");
    }

    private static void RecoveryLogRotation(string path)
    {
        string largeLog = new String('x', 256 * 1024);
        File.WriteAllText(path + ".log", largeLog);
        File.WriteAllText(path, "{ invalid json");
        AlarmStore store = new AlarmStore(path);
        Assert(store.Load().Count == 0 && File.Exists(store.RecoveryBackupPath), "Log rotation must not interfere with preserving the corrupt alarm data.");
        Assert(File.ReadAllText(path + ".log.previous") == largeLog, "Log rotation should retain the immediately previous log.");
        Assert(new FileInfo(path + ".log").Length < 256 * 1024, "The active recovery log should restart at a bounded size.");
        string firstBackup = store.RecoveryBackupPath;
        File.WriteAllText(path + ".log", largeLog);
        File.WriteAllText(path, "{ another invalid document");
        store.Load();
        Assert(File.Exists(firstBackup) && File.Exists(store.RecoveryBackupPath), "Log rotation must not delete earlier corrupt alarm backups.");
        Assert(File.ReadAllText(path + ".log.previous") == largeLog, "Repeated rotation should replace only the previous generated log.");
    }

    private static void FailedSave(string path)
    {
        AlarmScheduler scheduler = new AlarmScheduler(new AlarmStore(path));
        AlarmEntry entry = scheduler.AddCountdown("kept", TimeSpan.FromMinutes(48), Now);
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Expect<IOException>(delegate { scheduler.Remove(entry.Id); });
            Assert(scheduler.Alarms.Count == 1, "An unsuccessful save must not change memory.");
            Expect<IOException>(delegate { scheduler.Reschedule(entry.Id, Now.AddMinutes(20), AlarmMode.Countdown, Now); });
            Assert(scheduler.Alarms[0].CountdownDuration == TimeSpan.FromMinutes(48), "A failed countdown edit must retain its original 48-minute interval.");
            Expect<IOException>(delegate { scheduler.Postpone(entry.Id, TimeSpan.FromMinutes(5), Now); });
            Assert(scheduler.Alarms[0].DueAt == entry.DueAt && scheduler.Alarms[0].CountdownDuration == entry.CountdownDuration, "A failed temporary postpone must preserve both deadline and repeat duration.");
            Expect<IOException>(delegate { scheduler.Poll(Now.AddMinutes(48)); });
            Assert(scheduler.Alarms[0].State == AlarmState.Scheduled, "A failed poll save must leave the reminder ready to retry.");
            Assert(scheduler.NextScheduledAt == entry.DueAt, "Failed edits and polling must retain the cached next deadline for retry.");
        }
        Assert(new AlarmStore(path).Load().Count == 1, "An unsuccessful save must preserve disk state.");
        Assert(new AlarmStore(path).Load()[0].CountdownDuration == TimeSpan.FromMinutes(48), "A failed persistence operation must preserve the saved countdown interval.");
        Assert(scheduler.Poll(Now.AddMinutes(48)).Count == 1, "A failed poll must trigger normally once storage is writable.");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
