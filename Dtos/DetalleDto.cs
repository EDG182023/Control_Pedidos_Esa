namespace EsaLogistica.Api.Dtos
{
    public class DetalleDto
    {
        public string Numero { get; set; } = "";
        public int Linea { get; set; }
        public string ProductoCodigo { get; set; } = "";
        public string ProductoCompaniaCodigo { get; set; } = "";
        public string? LoteCodigo { get; set; }
        public DateTime LoteVencimiento { get; set; }
        public string? Serie { get; set; }
        public int Cantidad { get; set; }
    }
}
