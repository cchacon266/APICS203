namespace CS203XAPI.Models
{
    public class SetPowerRequest
    {
        public string ReaderIP { get; set; }
        public int PowerLevel { get; set; } // Potencia en dBm
    }
}
