using System;
using System.Globalization;

namespace QingReminder.Core
{
    internal static class CountdownInput
    {
        internal static TimeSpan Parse(string hours, string minutes, string seconds)
        {
            // Accumulate before converting so inputs above 59 carry without integer overflow.
            decimal totalSeconds = (decimal)ReadNumber(hours) * 3600
                + (decimal)ReadNumber(minutes) * 60 + ReadNumber(seconds);
            if (totalSeconds <= 0) throw new ArgumentException("倒计时必须大于 0 秒。");
            if (totalSeconds > (decimal)AlarmScheduler.MaxDelay.TotalSeconds)
                throw new ArgumentException("倒计时不能超过 365 天。");
            return TimeSpan.FromTicks((long)totalSeconds * TimeSpan.TicksPerSecond);
        }

        private static long ReadNumber(string text)
        {
            long value;
            if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new ArgumentException("倒计时的小时、分钟和秒钟请输入非负整数。");
            return value;
        }
    }
}
