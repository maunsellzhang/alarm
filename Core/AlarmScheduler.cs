using System;
using System.Collections.Generic;

namespace QingReminder.Core
{
    public sealed class AlarmScheduler
    {
        public const int MaxTitleLength = 200;
        public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(365);
        private static readonly IList<AlarmEntry> EmptyAlarms = new List<AlarmEntry>().AsReadOnly();

        private readonly AlarmStore store;
        private List<AlarmEntry> alarms;
        private DateTimeOffset? nextScheduledAt;

        public AlarmScheduler(AlarmStore store)
        {
            if (store == null)
                throw new ArgumentNullException("store");
            this.store = store;
            alarms = CopyEntries(store.Load());
            bool recoveredRinging = false;
            foreach (AlarmEntry alarm in alarms)
            {
                if (alarm.State == AlarmState.Ringing)
                {
                    alarm.State = AlarmState.Scheduled;
                    recoveredRinging = true;
                }
            }
            if (recoveredRinging)
                store.Save(alarms);
            UpdateNextScheduledAt();
        }

        public event EventHandler Changed;

        public int Count { get { return alarms.Count; } }
        public DateTimeOffset? NextScheduledAt { get { return nextScheduledAt; } }

        public IList<AlarmEntry> Alarms
        {
            get
            {
                List<AlarmEntry> snapshot = CopyEntries(alarms);
                snapshot.Sort(delegate(AlarmEntry first, AlarmEntry second)
                {
                    int comparison = first.DueAt.CompareTo(second.DueAt);
                    return comparison == 0 ? StringComparer.Ordinal.Compare(first.Id, second.Id) : comparison;
                });
                return snapshot.AsReadOnly();
            }
        }

        public AlarmEntry AddAbsolute(string title, DateTimeOffset dueAt, DateTimeOffset now)
        {
            return Add(title, dueAt, AlarmMode.Absolute, now);
        }

        public AlarmEntry AddCountdown(string title, TimeSpan delay, DateTimeOffset now)
        {
            return Add(title, GetDelayedTime(delay, now), AlarmMode.Countdown, now);
        }

        public AlarmEntry Reschedule(string id, DateTimeOffset dueAt, AlarmMode mode, DateTimeOffset now)
        {
            return Reschedule(id, GetExisting(id).Title, dueAt, mode, now);
        }

        public AlarmEntry Reschedule(string id, string title, DateTimeOffset dueAt, AlarmMode mode, DateTimeOffset now)
        {
            ValidateSchedule(dueAt, mode, now);
            string normalizedTitle = NormalizeTitle(title);
            GetExisting(id);
            List<AlarmEntry> proposed = CopyEntries(alarms);
            AlarmEntry alarm = proposed.Find(delegate(AlarmEntry entry) { return entry.Id == id; });
            alarm.Title = normalizedTitle;
            alarm.DueAt = dueAt;
            alarm.Mode = mode;
            alarm.CountdownTicks = mode == AlarmMode.Countdown ? (long?)(dueAt - now).Ticks : null;
            alarm.State = AlarmState.Scheduled;
            Commit(proposed);
            return alarm.Copy();
        }

        public AlarmEntry Postpone(string id, TimeSpan delay, DateTimeOffset now)
        {
            DateTimeOffset dueAt = GetDelayedTime(delay, now);
            ValidateSchedule(dueAt, AlarmMode.Countdown, now);
            GetExisting(id);
            List<AlarmEntry> proposed = CopyEntries(alarms);
            AlarmEntry alarm = proposed.Find(delegate(AlarmEntry entry) { return entry.Id == id; });
            alarm.DueAt = dueAt;
            alarm.Mode = AlarmMode.Countdown;
            if (!alarm.CountdownTicks.HasValue)
                alarm.CountdownTicks = delay.Ticks;
            alarm.State = AlarmState.Scheduled;
            Commit(proposed);
            return alarm.Copy();
        }

        public bool Complete(string id)
        {
            return Remove(id);
        }

        public bool Remove(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
                throw new ArgumentException("An alarm identifier is required.", "id");
            if (!alarms.Exists(delegate(AlarmEntry alarm) { return alarm.Id == id; }))
                return false;
            List<AlarmEntry> proposed = CopyEntries(alarms);
            if (proposed.RemoveAll(delegate(AlarmEntry alarm) { return alarm.Id == id; }) == 0)
                return false;
            Commit(proposed);
            return true;
        }

        public IList<AlarmEntry> Poll(DateTimeOffset now)
        {
            // Idle polling must not clone every reminder or allocate new empty results.
            if (!nextScheduledAt.HasValue || now < nextScheduledAt.Value)
                return EmptyAlarms;
            List<AlarmEntry> proposed = CopyEntries(alarms);
            List<AlarmEntry> due = new List<AlarmEntry>();
            foreach (AlarmEntry alarm in proposed)
            {
                if (alarm.State == AlarmState.Scheduled && alarm.DueAt <= now)
                {
                    alarm.State = AlarmState.Ringing;
                    due.Add(alarm.Copy());
                }
            }
            if (due.Count != 0)
                Commit(proposed);
            due.Sort(delegate(AlarmEntry first, AlarmEntry second) { return first.DueAt.CompareTo(second.DueAt); });
            return due.AsReadOnly();
        }

        private AlarmEntry Add(string title, DateTimeOffset dueAt, AlarmMode mode, DateTimeOffset now)
        {
            ValidateSchedule(dueAt, mode, now);
            AlarmEntry alarm = new AlarmEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = NormalizeTitle(title),
                DueAt = dueAt,
                CreatedAt = now,
                Mode = mode,
                State = AlarmState.Scheduled,
                CountdownTicks = mode == AlarmMode.Countdown ? (long?)(dueAt - now).Ticks : null
            };
            List<AlarmEntry> proposed = CopyEntries(alarms);
            proposed.Add(alarm);
            Commit(proposed);
            return alarm.Copy();
        }

        private void Commit(List<AlarmEntry> proposed)
        {
            store.Save(proposed);
            alarms = proposed;
            UpdateNextScheduledAt();
            EventHandler handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void UpdateNextScheduledAt()
        {
            nextScheduledAt = null;
            foreach (AlarmEntry alarm in alarms)
            {
                if (alarm.State == AlarmState.Scheduled && (!nextScheduledAt.HasValue || alarm.DueAt < nextScheduledAt.Value))
                    nextScheduledAt = alarm.DueAt;
            }
        }

        private AlarmEntry GetExisting(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
                throw new ArgumentException("An alarm identifier is required.", "id");
            AlarmEntry existing = alarms.Find(delegate(AlarmEntry alarm) { return alarm.Id == id; });
            if (existing == null)
                throw new KeyNotFoundException("The alarm no longer exists.");
            return existing;
        }

        private static string NormalizeTitle(string title)
        {
            string normalized = String.IsNullOrWhiteSpace(title) ? "\u63d0\u9192" : title.Trim();
            if (normalized.Length > MaxTitleLength)
                throw new ArgumentException("The title cannot exceed " + MaxTitleLength + " characters.", "title");
            return normalized;
        }

        private static void ValidateSchedule(DateTimeOffset dueAt, AlarmMode mode, DateTimeOffset now)
        {
            if (!Enum.IsDefined(typeof(AlarmMode), mode))
                throw new ArgumentOutOfRangeException("mode");
            if (now == default(DateTimeOffset))
                throw new ArgumentOutOfRangeException("now", "The current time is invalid.");
            if (dueAt <= now)
                throw new ArgumentOutOfRangeException("dueAt", "The reminder time must be in the future.");
            if (mode == AlarmMode.Countdown && dueAt - now > MaxDelay)
                throw new ArgumentOutOfRangeException("dueAt", "A countdown cannot exceed 365 days.");
        }

        private static DateTimeOffset GetDelayedTime(TimeSpan delay, DateTimeOffset now)
        {
            if (delay <= TimeSpan.Zero || delay > MaxDelay)
                throw new ArgumentOutOfRangeException("delay", "The delay must be positive and cannot exceed 365 days.");
            return now.Add(delay);
        }

        private static List<AlarmEntry> CopyEntries(IEnumerable<AlarmEntry> entries)
        {
            ICollection<AlarmEntry> collection = entries as ICollection<AlarmEntry>;
            List<AlarmEntry> copies = collection == null ? new List<AlarmEntry>() : new List<AlarmEntry>(collection.Count);
            foreach (AlarmEntry entry in entries)
                copies.Add(entry.Copy());
            return copies;
        }
    }
}
