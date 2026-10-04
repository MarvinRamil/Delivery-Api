namespace BeeLogistics.Api.Middleware;

/// <summary>
/// Adds comprehensive security headers to all responses (OWASP recommendations)
/// Enhanced with additional security measures for defense in depth
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // A03:2021 - Injection: Content Security Policy (Enhanced)
        // Strict CSP to prevent XSS, injection attacks, and data exfiltration
        // Allow Cloudflare Insights and blob: workers for MQTT
        // 
        // SECURITY NOTE: 'unsafe-inline' and 'unsafe-eval' are required for:
        // - SignalR WebSocket connections (dynamic script injection)
        // - Swagger UI (dynamic API documentation generation)
        // 
        // This is a known trade-off: SAST/DAST tools will flag this as MEDIUM severity.
        // Alternative solutions:
        // 1. Use nonces for SignalR (requires frontend changes)
        // 2. Disable Swagger in production (already done via environment check)
        // 3. Use separate CSP for Swagger endpoints (future enhancement)
        //
        // Current mitigation: XSS protection via input validation, output encoding, and other security headers
        // NOTE: CSP is mainly for web pages, not API endpoints - skip for API to avoid CORS conflicts
        if (!context.Request.Path.StartsWithSegments("/api") && 
            !context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.Headers.Append("Content-Security-Policy", 
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://static.cloudflareinsights.com https://cdn.jsdelivr.net blob:; " + // Allow Cloudflare Insights, Scalar CDN, and blob workers
                "script-src-elem 'self' 'unsafe-inline' https://static.cloudflareinsights.com https://cdn.jsdelivr.net; " + // Explicit script-src-elem for Cloudflare and Scalar
                "worker-src 'self' blob:; " + // Allow blob: workers for MQTT
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " + // Allow Scalar styles
                "img-src 'self' data: blob: https:; " +
                "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net data:; " + // Allow Scalar fonts
                "connect-src 'self' wss: ws: https: http: https://static.cloudflareinsights.com; " + // Allow Cloudflare Insights
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "object-src 'none'; " +
                "upgrade-insecure-requests");
        }
        
        // A05:2021 - Security Misconfiguration: Prevent MIME sniffing
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        
        // Clickjacking protection - Use SAMEORIGIN for better compatibility (scanner recommendation)
        // DENY is more secure but can break iframes on same origin
        context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
        
        // XSS Protection (legacy browsers + modern)
        context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
        
        // Referrer Policy - don't leak URLs
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        
        // Permissions Policy - disable unused browser features
        // Removed deprecated features: speaker, vibrate
        context.Response.Headers.Append("Permissions-Policy", 
            "geolocation=(self), " +
            "microphone=(), " +
            "camera=(), " +
            "magnetometer=(), " +
            "gyroscope=(), " +
            "fullscreen=(self), " +
            "payment=()");
        
        // Strict Transport Security (HSTS) - Force HTTPS
        // Always set HSTS (Cloudflare handles HTTPS termination)
        // Scanner requires: max-age=31536000; includeSubDomains
        context.Response.Headers.Append("Strict-Transport-Security", 
            "max-age=31536000; includeSubDomains");
        
        // Cache control for sensitive data - prevent caching
        if (context.Request.Path.StartsWithSegments("/api") || 
            context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.Headers.Append("Cache-Control", 
                "no-store, no-cache, must-revalidate, max-age=0, proxy-revalidate");
            context.Response.Headers.Append("Pragma", "no-cache");
            context.Response.Headers.Append("Expires", "0");
        }
        else
        {
            // Static files can be cached
            context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
        }
        
        // Cross-Origin Embedder Policy
        // Skip for API endpoints to avoid CORS conflicts
        // APIs don't need COEP - it's mainly for web pages that embed resources
        if (!context.Request.Path.StartsWithSegments("/api") && 
            !context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.Headers.Append("Cross-Origin-Embedder-Policy", "credentialless");
        }
        
        // Cross-Origin Opener Policy
        // Skip for API endpoints to avoid CORS conflicts
        if (!context.Request.Path.StartsWithSegments("/api") && 
            !context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.Headers.Append("Cross-Origin-Opener-Policy", "same-origin");
        }
        
        // Cross-Origin Resource Policy
        // 'cross-origin' allows resources from other origins (needed for map tiles and CORS)
        context.Response.Headers.Append("Cross-Origin-Resource-Policy", "cross-origin");
        
        // Remove server identification headers (security through obscurity)
        // BUT: Don't remove CORS headers! Skip for API endpoints
        if (!context.Request.Path.StartsWithSegments("/api") && 
            !context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.Headers.Remove("Server");
            context.Response.Headers.Remove("X-Powered-By");
            context.Response.Headers.Remove("X-AspNet-Version");
            context.Response.Headers.Remove("X-AspNetMvc-Version");
        }
        
        // Additional security headers
        context.Response.Headers.Append("X-Download-Options", "noopen"); // Prevent IE from executing downloads
        context.Response.Headers.Append("X-Permitted-Cross-Domain-Policies", "none"); // Prevent Flash/PDF cross-domain

        await _next(context);
    }
}
