using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;

namespace BeeLogistics.Api.Extensions;

public static class SwaggerExtensions
{
    /// <summary>
    /// Adds Swagger/OpenAPI documentation services to the service collection
    /// </summary>
    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "BeeLogistics API",
                Description = "API documentation for BeeLogistics Management System"
            });

            // Add JWT Bearer authentication
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Bearer token. Paste your token (without 'Bearer ' prefix).",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        return services;
    }

    /// <summary>
    /// Configures Swagger/OpenAPI middleware in the application pipeline
    /// </summary>
    public static IApplicationBuilder UseSwaggerDocumentation(this IApplicationBuilder app, bool isDevelopment = false)
    {
        app.UseSwagger(options =>
        {
            options.RouteTemplate = "swagger/{documentName}/swagger.json";
        });

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "BeeLogistics API v1");
            options.RoutePrefix = "swagger";
        });

        return app;
    }
}
