using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// CORS for the React dev server we'll add later
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "https://localhost:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()));

// OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Cost Analyzer API", Version = "v1" });

    // Add a Bearer auth scheme for Swagger UI's "Authorize" button
    var bearerScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Paste your access token (no 'Bearer ' prefix).",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    c.AddSecurityDefinition("Bearer", bearerScheme);

    // Apply globally (Swagger will include the header on all calls)
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { bearerScheme, Array.Empty<string>() }
    });
});

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;       // preserve PascalCase
    options.SerializerOptions.DictionaryKeyPolicy = null;
});

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("pg")));

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var devIssuer = (builder.Configuration["Auth:Dev:Issuer"] ?? "dev-issuer").Trim();
var devKey = builder.Configuration["Auth:Dev:Key"];

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = devIssuer,                  // must equal token's "iss"
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(devKey!)),
            NameClaimType = "username",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine("JWT auth failed: " + ctx.Exception.Message);
                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                var iss = ctx.Principal?.FindFirst("iss")?.Value;
                Console.WriteLine($"JWT validated. iss='{iss}', expected='{devIssuer}'");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Simple health check
app.MapGet("/health", () => Results.Ok(new { ok = true, timeUtc = DateTime.UtcNow }))
   .WithTags("System")
   .AllowAnonymous();

// Core calculator endpoint (no auth yet; we’ll add later)
app.MapPost("/calculate", (CalculateRequest r) =>
{
    var totalDeclaredValueUsd = r.DeclaredUnitValueUsd * r.UnitCount;

    var freightCostUsd = r.IsCifShipment ? 0 : r.FreightCostUsd;
    var insuranceCostUsd = r.IsCifShipment ? 0 : totalDeclaredValueUsd * r.InsuranceRatePercent;

    var costInsuranceFreightUsd = totalDeclaredValueUsd + freightCostUsd + insuranceCostUsd;

    var dutyCostUsd = costInsuranceFreightUsd * r.DutyRatePercent;
    var valueAddedTaxBaseUsd = costInsuranceFreightUsd + dutyCostUsd + r.DestinationChargesUsd;
    var valueAddedTaxUsd = valueAddedTaxBaseUsd * r.ValueAddedTaxRatePercent;
    var otherTaxesUsd = costInsuranceFreightUsd * r.OtherTaxesRatePercent;

    var bankFeeUsd = totalDeclaredValueUsd * r.BankForeignExchangeSpreadPercent;
    var paymentFeeUsd = totalDeclaredValueUsd * r.PaymentFeePercent;

    var landedCostUsd =
        totalDeclaredValueUsd +
        freightCostUsd +
        insuranceCostUsd +
        dutyCostUsd +
        valueAddedTaxUsd +
        otherTaxesUsd +
        r.OriginChargesUsd +
        r.DestinationChargesUsd +
        r.CustomsBrokerUsd +
        bankFeeUsd +
        paymentFeeUsd;

    var landedCostCop = landedCostUsd * r.UsdToCopRate + r.LastMileCop + r.MiscellaneousAdminCostCop;
    var unitCostCop = landedCostCop / r.UnitCount;

    var channelFeesCop = r.SalePriceCop * (r.CommissionPercent + r.PaymentGatewayPercent) + r.FulfillmentFeeCop;
    var unitGrossProfitCop = r.SalePriceCop - channelFeesCop - unitCostCop;
    var unitGrossMarginPercent = r.SalePriceCop == 0 ? 0 : unitGrossProfitCop / r.SalePriceCop;
    var breakEvenPriceCop = (unitCostCop + r.FulfillmentFeeCop) / (1 - r.CommissionPercent - r.PaymentGatewayPercent);

    var result = new CalculateResult(
        LandedCostUsd: Math.Round(landedCostUsd, 2),
        LandedCostCop: Math.Round(landedCostCop, 0),
        UnitCostCop: Math.Round(unitCostCop, 0),
        UnitGrossProfitCop: Math.Round(unitGrossProfitCop, 0),
        UnitGrossMarginPercent: Math.Round(unitGrossMarginPercent * 100m, 2),
        BreakEvenPriceCop: Math.Round(breakEvenPriceCop, 0)
    );

    return Results.Ok(result);
})
.WithTags("Calculator")
.AllowAnonymous();

app.MapPost("/dev/token", (TokenRequest req) =>
{
    var creds = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(devKey!)),
        SecurityAlgorithms.HmacSha256);

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, req.Sub ?? Guid.NewGuid().ToString()),
        new("username", req.Username ?? "devuser"),
        new("token_use", "access")
    };

    if (req.Roles is { Length: > 0 })
    {
        foreach (var r in req.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));  // for policies expecting ClaimTypes.Role
        }
    }

    var now = DateTime.UtcNow;
    var notBefore = now.AddSeconds(-5); // small backshift to tolerate clock jitter

    var lifetime = TimeSpan.FromHours(req.HoursValid.GetValueOrDefault(1));
    if (lifetime <= TimeSpan.Zero)
        lifetime = TimeSpan.FromMinutes(5); // minimum lifetime

    var expires = now.Add(lifetime);

    var jwt = new JwtSecurityToken(
        issuer: devIssuer,
        audience: null,
        claims: claims,
        notBefore: notBefore,
        expires: expires,
        signingCredentials: creds
        );

    var token = new JwtSecurityTokenHandler().WriteToken(jwt);
    return Results.Ok(new { token, expiresUtc = expires });
})
.WithTags("Dev")
.AllowAnonymous();


app.MapGet("/scenarios", async (AppDbContext db) =>
    await db.Scenarios.OrderByDescending(s => s.CreatedUtc).ToListAsync()
)
.WithTags("Scenarios")
.RequireAuthorization();

app.MapPost("/scenarios", async (Scenario s, AppDbContext db) =>
{
    if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
    s.CreatedUtc = DateTime.UtcNow;
    db.Scenarios.Add(s);
    await db.SaveChangesAsync();
    return Results.Created($"/scenarios/{s.Id}", s);
})
.WithTags("Scenarios")
.RequireAuthorization("AdminOnly");

app.MapGet("/debug-claims", (System.Security.Claims.ClaimsPrincipal u) =>
    Results.Json(u.Claims.Select(c => new { c.Type, c.Value })))
.RequireAuthorization()
.WithOpenApi(op =>
{
    op.Security = new List<OpenApiSecurityRequirement>
    {
        new()
        {
            [ new OpenApiSecurityScheme
                { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }
            ] = Array.Empty<string>()
        }
    };
    return op;
});

app.MapGet("/debug-auth", (ClaimsPrincipal u) =>
{
    var id = u.Identities.FirstOrDefault();
    return Results.Json(new
    {
        roleClaimType = id?.RoleClaimType,
        roles = u.Claims.Where(c => c.Type == (id?.RoleClaimType ?? "")).Select(c => c.Value).ToArray(),
        isAdmin = u.IsInRole("Admin")
    });
})
.RequireAuthorization();

app.Run();

public record CalculateRequest(
  int UnitCount,
  decimal DeclaredUnitValueUsd,
  decimal FreightCostUsd,
  decimal InsuranceRatePercent,
  decimal OriginChargesUsd,
  decimal DestinationChargesUsd,
  decimal CustomsBrokerUsd,
  decimal DutyRatePercent,
  decimal ValueAddedTaxRatePercent,
  decimal OtherTaxesRatePercent,
  decimal BankForeignExchangeSpreadPercent,
  decimal PaymentFeePercent,
  decimal UsdToCopRate,
  decimal SalePriceCop,
  decimal CommissionPercent,
  decimal PaymentGatewayPercent,
  decimal FulfillmentFeeCop,
  decimal LastMileCop,
  decimal MiscellaneousAdminCostCop,
  bool IsCifShipment
);

public record CalculateResult(
  decimal LandedCostUsd,
  decimal LandedCostCop,
  decimal UnitCostCop,
  decimal UnitGrossProfitCop,
  decimal UnitGrossMarginPercent,
  decimal BreakEvenPriceCop
);

public record TokenRequest(string? Username, string? Sub, string[]? Roles, int? HoursValid);

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Scenario> Scenarios => Set<Scenario>();
}

public class Scenario
{
    public Guid Id { get; set; } = Guid.NewGuid(); 
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow; 
}

