namespace Allowlist.Api.Endpoints;

public sealed class SpaRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        var path = httpContext?.Request.Path ?? new PathString("/" + values[routeKey]);
        return !path.StartsWithSegments("/api") && !path.StartsWithSegments("/scalar") &&
            !path.StartsWithSegments("/openapi") && !path.StartsWithSegments("/health");
    }
}
