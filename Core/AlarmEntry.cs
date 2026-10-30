using System;
using System.Runtime.Serialization;

namespace QingReminder.Core
{
    [DataContract]
    public enum AlarmMode
    {
        [EnumMember] Absolute = 0,
        [EnumMember] Countdown = 1
    }

    [DataContract]
    public enum AlarmState
    {
        [EnumMember] Scheduled = 0,
        [EnumMember] Ringing = 1
    }

    [DataContract]
    public sealed class AlarmEntry
    {
        [DataMember(Order = 1)]
        public string Id { get; internal set; }

        [DataMember(Order = 2)]
        public string Title { get; internal set; }

        [DataMember(Order = 3)]
        public DateTimeOffset DueAt { get; internal set; }

        [DataMember(Order = 4)]
        public DateTimeOffset CreatedAt { get; internal set; }

        [DataMember(Order = 5)]
        public AlarmMode Mode { get; internal set; }

        [DataMember(Order = 6)]
        public AlarmState State { get; internal set; }

        [DataMember(Order = 7, EmitDefaultValue = false)]
        public long? CountdownTicks { get; internal set; }

        public TimeSpan? CountdownDuration
        {
            get { return CountdownTicks.HasValue ? (TimeSpan?)TimeSpan.FromTicks(CountdownTicks.Value) : null; }
        }

        internal AlarmEntry Copy()
        {
            return new AlarmEntry
            {
                Id = Id,
                Title = Title,
                DueAt = DueAt,
                CreatedAt = CreatedAt,
                Mode = Mode,
                State = State,
                CountdownTicks = CountdownTicks
            };
        }
    }
}
