using System.Net;
using BH3.Game.Operations;
using BH3.Server.Transport;

namespace BH3.Server.Hosting;

public static class GmEndpoints
{
    public static void Map(WebApplication app,ServerComposition composition,UdpGameHost udp)
    {
        app.Use(async(ctx,next)=>
        {
            if(ctx.Request.Path.StartsWithSegments("/gm")||ctx.Request.Path.StartsWithSegments("/api/gm"))
            {
                string host=ctx.Request.Host.Host;
                bool localHost=host=="localhost"||IPAddress.TryParse(host,out var ip)&&IPAddress.IsLoopback(ip);
                if(!localHost||ctx.Connection.RemoteIpAddress is not {} remote||!IPAddress.IsLoopback(remote)) {ctx.Response.StatusCode=403;return;}
                if(ctx.Request.Method!="GET" && (ctx.Request.Headers["X-BH3-GM"]!="1" || ctx.Request.Headers.TryGetValue("Origin",out var origin) && origin!=$"{ctx.Request.Scheme}://{ctx.Request.Host}")) {ctx.Response.StatusCode=403;return;}
                ctx.Response.Headers.CacheControl="no-store";
                ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
                ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; object-src 'none'";
            }
            await next(ctx);
        });
        app.UseStaticFiles();
        app.MapGet("/gm",()=>Results.Redirect("/gm/index.html"));
        app.MapGet("/api/gm/accounts",()=>composition.Gm.Accounts());
        app.MapGet("/api/gm/shop-catalog",()=>new{goods=GmService.ShopCatalog.Goods,shops=GmService.ShopCatalog.Shops.Select(x=>new{x.Id,x.Name,x.Mall})});
        app.MapGet("/api/gm/catalog",(string? kind,string? q,int? offset)=>
        {
            var items=GrantService.Catalog.Items.Where(x=>(string.IsNullOrEmpty(kind)||x.Kind==kind)&&(string.IsNullOrWhiteSpace(q)||x.Name.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Id.ToString().Contains(q))).ToArray();
            return Results.Json(new{total=items.Length,items=items.Skip(Math.Max(0,offset??0)).Take(100)});
        });
        app.MapGet("/api/gm/player/{uid}",(uint uid)=>
        {
            try{return Results.Json(composition.Gm.Snapshot(uid));}catch(InvalidOperationException){return Results.NotFound(new{error="本地账号尚未登录，请先登录一次。"});}
        });
        app.MapPost("/api/gm/player/{uid}",(uint uid,GmCommand command)=>
        {
            try{return Results.Json(udp.ApplyGm(uid,()=>composition.Gm.Execute(uid,command),s=>composition.Lobby.GmNotifications(s,command.Action=="mail")));}
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or OverflowException)
            {return Results.BadRequest(new{error=ex.Message});}
        });
    }
}
