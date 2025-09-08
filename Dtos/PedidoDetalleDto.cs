namespace EsaLogistica.Api.Dtos
{
    /// <summary>
    /// Representa un ítem de detalle para un pedido TXT.
    /// </summary>
    public class PedidoDetalleDto
    {
        public int linea { get; set; }
        public string productoCompaniaCodigo { get; set; } = string.Empty;
        public string productoCodigo { get; set; } = string.Empty;
        public string loteCodigo { get; set; } = string.Empty;
        public string serie { get; set; } = string.Empty;
        public int cantidad { get; set; }
        public bool despachoParcial { get; set; }
    }
}