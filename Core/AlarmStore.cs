using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace QingReminder.Core
{
    public sealed class AlarmStore
    {
        private const long MaxRecoveryLogBytes = 256 * 1024;

        [DataContract]
        private sealed class StoreDocument
        {
            [DataMember(Order = 1, IsRequired = true)]
            public int Version { get; set; }

            [DataMember(Order = 2, IsRequired = true)]
            public List<AlarmEntry> Alarms { get; set; }
        }

        private readonly string path;

        public AlarmStore(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A storage path is required.", "path");
            this.path = Path.GetFullPath(path);
        }

        public string FilePath { get { return path; } }
        public string LastRecoveryMessage { get; private set; }
        public string RecoveryBackupPath { get; private set; }

        public IList<AlarmEntry> Load()
        {
            LastRecoveryMessage = null;
            RecoveryBackupPath = null;
            if (!File.Exists(path))
                return new List<AlarmEntry>().AsReadOnly();

            try
            {
                StoreDocument document;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    document = (StoreDocument)CreateSerializer().ReadObject(stream);
                if (document == null)
                    throw new InvalidDataException("The alarm document is empty.");
                if (document.Version != 1)
                    throw new NotSupportedException("Unsupported alarm data version: " + document.Version);
                ValidateEntries(document.Alarms);
                foreach (AlarmEntry alarm in document.Alarms)
                {
                    if (alarm.Mode == AlarmMode.Countdown && !alarm.CountdownTicks.HasValue)
                    {
                        TimeSpan originalDelay = alarm.DueAt - alarm.CreatedAt;
                        if (originalDelay > TimeSpan.Zero && originalDelay <= AlarmScheduler.MaxDelay)
                            alarm.CountdownTicks = originalDelay.Ticks;
                    }
                }
                return CopyEntries(document.Alarms).AsReadOnly();
            }
            catch (SerializationException exception)
            {
                return RecoverInvalidFile(exception);
            }
            catch (XmlException exception)
            {
                return RecoverInvalidFile(exception);
            }
            catch (InvalidDataException exception)
            {
                return RecoverInvalidFile(exception);
            }
            catch (FormatException exception)
            {
                return RecoverInvalidFile(exception);
            }
            catch (ArgumentException exception)
            {
                return RecoverInvalidFile(exception);
            }
        }

        public void Save(IEnumerable<AlarmEntry> alarms)
        {
            if (alarms == null)
                throw new ArgumentNullException("alarms");
            List<AlarmEntry> entries = CopyEntries(alarms);
            ValidateEntries(entries);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CreateSerializer().WriteObject(stream, new StoreDocument { Version = 1, Alarms = entries });
                    stream.Flush(true);
                }

                // Keep the previous valid document while atomically replacing the active file.
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, path + ".bak", true);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private IList<AlarmEntry> RecoverInvalidFile(Exception exception)
        {
            string backupPath = path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "." + Guid.NewGuid().ToString("N") + ".json";
            File.Move(path, backupPath);
            RecoveryBackupPath = backupPath;
            LastRecoveryMessage = "Invalid alarm data was preserved at " + backupPath + ". " + exception.Message;
            try
            {
                string logPath = path + ".log";
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxRecoveryLogBytes)
                {
                    string previousLogPath = logPath + ".previous";
                    if (File.Exists(previousLogPath))
                        File.Delete(previousLogPath);
                    File.Move(logPath, previousLogPath);
                }
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " " + LastRecoveryMessage + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                LastRecoveryMessage += " The recovery log could not be written.";
            }
            catch (UnauthorizedAccessException)
            {
                LastRecoveryMessage += " The recovery log could not be written.";
            }
            return new List<AlarmEntry>().AsReadOnly();
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(typeof(StoreDocument));
        }

        private static List<AlarmEntry> CopyEntries(IEnumerable<AlarmEntry> alarms)
        {
            ICollection<AlarmEntry> collection = alarms as ICollection<AlarmEntry>;
            List<AlarmEntry> copies = collection == null ? new List<AlarmEntry>() : new List<AlarmEntry>(collection.Count);
            foreach (AlarmEntry alarm in alarms)
                copies.Add(alarm == null ? null : alarm.Copy());
            return copies;
        }

        private static void ValidateEntries(IList<AlarmEntry> alarms)
        {
            if (alarms == null)
                throw new InvalidDataException("The alarm list is missing.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (AlarmEntry alarm in alarms)
            {
                if (alarm == null || String.IsNullOrWhiteSpace(alarm.Id) || !ids.Add(alarm.Id))
                    throw new InvalidDataException("An alarm has a missing or duplicate identifier.");
                if (String.IsNullOrWhiteSpace(alarm.Title) || alarm.Title.Length > AlarmScheduler.MaxTitleLength)
                    throw new InvalidDataException("An alarm title is invalid.");
                if (!Enum.IsDefined(typeof(AlarmMode), alarm.Mode) || !Enum.IsDefined(typeof(AlarmState), alarm.State))
                    throw new InvalidDataException("An alarm mode or state is invalid.");
                if (alarm.CreatedAt == default(DateTimeOffset) || alarm.DueAt == default(DateTimeOffset))
                    throw new InvalidDataException("An alarm timestamp is missing.");
                if (alarm.CountdownTicks.HasValue && (alarm.CountdownTicks.Value <= 0 || alarm.CountdownTicks.Value > AlarmScheduler.MaxDelay.Ticks))
                    throw new InvalidDataException("An alarm countdown duration is invalid.");
            }
        }
    }
}
