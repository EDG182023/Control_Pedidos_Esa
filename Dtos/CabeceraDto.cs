namespace EsaLogistica.Api.Dtos
{
    public class CabeceraDto
    {
        public string TipoCodigo { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Sucursal { get; set; } = "";
        public string Numero { get; set; } = "";
        public DateTime FechaEmision { get; set; }
        public DateTime FechaEntrega { get; set; }
        public string ClienteCodigo { get; set; } = "";
        public int? Bultos { get; set; }
        public decimal? Kilos { get; set; }
        public decimal? M3 { get; set; }
        public string? SubClienteCodigo { get; set; }
        public string RazonSocial { get; set; } = "";
        public string? DepositoCodigo { get; set; }
        public string LocalidadNombre { get; set; } = "";
        public string CodigoPostal { get; set; } = "";
        public string Direccion { get; set; } = "";
        public decimal? ValorDeclarado { get; set; }
        public string? ReferenciaA { get; set; }
        public string? ReferenciaB { get; set; }
        public string? Observaciones { get; set; }
        public string? Telefono { get; set; }
        public string? Email { get; set; }
        // enrich de validación de dirección
        public string? AreaMuelle { get; set; }     // ya lo estabas usando desde COD_POS
        public string? DireccionNormalizada { get; set; }
        public double? Lat { get; set; }
        public double? Lng { get; set; }
        public string? ProvinciaNormalizada { get; set; }
        public string? LocalidadNormalizada { get; set; }
    }
}
