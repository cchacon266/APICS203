namespace CS203XAPI.Models
{
    public class GpioRequest
    {
        public string Action { get; set; }
        public int Gpio { get; set; }
        public bool State { get; set; } 
        public string ReaderIP { get; set; }
    }
}
