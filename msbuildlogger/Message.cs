using System;

namespace MSBuildLogger
{
    public enum Severity
    {
        Info,
        Warning,
        Error
    }

    public struct Message
    {
        public string ProjectFile { get; set; }

        public String Description { get; set; }
        
        public String Code { get; set; }
    
        public Severity Severity { get; set; }

        public String File { get; set; }

        public int Line { get; set; }

        public int Column { get; set; }
    }
}
