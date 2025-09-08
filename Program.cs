using EsaLogistica.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Servicios propios - ORDEN IMPORTANTE: ApiService antes de ValidationService
builder.Services.AddScoped<ITxtParserService, TxtParserService>();

// HttpClient para ApiService
builder.Services.AddHttpClient<IApiService, ApiService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ExternalApi:BaseUrl"]!);
});

// ValidationService depende de IApiService
builder.Services.AddScoped<IValidationService, ValidationService>();

// CargaService depende de ValidationService
builder.Services.AddScoped<ICargaService, CargaService>();

// HttpClients tipados para validación de direcciones
builder.Services.AddHttpClient<GeorefArAddressValidationService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AddressValidation:Georef:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("AddressValidation:Georef:TimeoutSeconds", 5));
});

builder.Services.AddHttpClient<NominatimAddressValidationService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AddressValidation:Nominatim:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("AddressValidation:Nominatim:TimeoutSeconds", 5));
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EsaLogistica/1.0 (+validacion-direccion)");
});

builder.Services.AddScoped<IAddressValidationService>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var prov = cfg["AddressValidation:Provider"]?.ToLowerInvariant();
    return prov switch
    {
        "nominatim" => sp.GetRequiredService<NominatimAddressValidationService>(),
        _ => sp.GetRequiredService<GeorefArAddressValidationService>()
    };
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
                "http://192.168.1.235:3000",
                "https://192.168.1.235:3000",
                "http://localhost:3000",
                "https://localhost:3000"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "EsaLogistica API v1");
    c.RoutePrefix = string.Empty; // Esto hace que Swagger esté en la raíz
});

app.UseCors("AllowFrontend");
app.MapControllers();
app.Run();