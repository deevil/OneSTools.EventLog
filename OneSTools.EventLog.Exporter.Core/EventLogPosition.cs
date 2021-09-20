namespace OneSTools.EventLog.Exporter.Core
{
    public class EventLogPosition
    {
        public EventLogPosition(string infobaseName, string fileName, long endPosition, long lgfEndPosition, long id)
        {
            InfobaseName = infobaseName;
            FileName = fileName;
            EndPosition = endPosition;
            LgfEndPosition = lgfEndPosition;
            Id = id;
        }

        public string InfobaseName { get; }
        public string FileName { get; }
        public long EndPosition { get; }
        public long LgfEndPosition { get; }
        public long Id { get; }
    }
}