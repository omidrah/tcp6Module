using System;

namespace TCPServer
{
    public class Machine
    {
        public string IMEI1 { get; set; }
        public string IMEI2 { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } //6 or 1 module
        public DateTime? CreateDate { get; set; }
    }
}