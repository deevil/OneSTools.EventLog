namespace OneSTools.EventLog.Exporter.Core.Splunk
{
    public class SplunkStorageSetting
    {
        public string DB { get; set; } = "";
        // Path to event log possition 
        public string Path {get; set; } = "";
        public string Host { get; set; } = "";
        public string Token { get; set; } = "";
        public int SplunkTimeout { get; set; } = 30;
    }
}