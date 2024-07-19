namespace CS203XAPI.Models
{
    public class SetPowerRequest
    {
        public string ReaderIP { get; set; } // Dirección IP del lector
        public int PowerLevel { get; set; } // Nivel de potencia deseado en dBm
    }
}
