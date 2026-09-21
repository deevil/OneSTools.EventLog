using System;

namespace OneSTools.EventLog
{
    public class EventLogPositionInvalidException : Exception
    {
        public string FileName { get; }
        public long Position { get; }

        public EventLogPositionInvalidException(string fileName, long position)
            : base($"Log file \"{fileName}\" at saved position {position} is not at an event boundary! File was likely recreated or overwritten. Manual intervention required.")
        {
            FileName = fileName;
            Position = position;
        }
    }
}
