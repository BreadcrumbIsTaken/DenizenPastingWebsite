using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DenizenPastingWebsite
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            PasteDatabase.Init();
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error/Any");
            }
            PasteServer.LoadConfig();
            app.Use(async (context, next) =>
            {
                string path = context.Request.Path.Value.ToLowerFast();
                if (path.StartsWith("/view/") && !path.StartsWith("/view/index"))
                {
                    context.Items["viewable"] = path[("/view/".Length)..];
                    context.Request.Path = context.Request.Method == "POST" ? "/New/Edit" : "/View/Index";
                }
                else if (path.StartsWith("/edit/"))
                {
                    context.Items["editing"] = path[("/edit/".Length)..];
                    context.Request.Path = "/New/Edit";
                }
                await next();
            });
            app.Use(async (context, next) =>
            {
                await next();
                if (context.Response.StatusCode == 404)
                {
                    string path = context.Request.Path.Value.ToLowerFast();
                    if (!path.StartsWith("/error/"))
                    {
                        context.Request.Path = "/Error/Error404";
                        await next();
                    }
                }
            });
            app.UseStaticFiles();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=New}/{action=Index}/{id?}");
            });
        }
    }
}
