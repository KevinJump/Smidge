﻿using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Smidge.CompositeFiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;
using System.Linq;
using Smidge.Models;
using Microsoft.Extensions.Options;
using Smidge.Options;
using Smidge.FileProcessors;
using Smidge.Hashing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http.Extensions;
using Smidge.Cache;
using System.Collections.Concurrent;

[assembly: InternalsVisibleTo("Smidge.Tests")]

namespace Smidge
{
    //cross cutting? Try to avoid extra installation steps? https://cetus.io/tim/ASPNET-Core-2.0-Stripping-Away-Cross-Cutting-Concerns/
    //public class SmidgeHostBuild : IHostingStartup
    //{
    //    public void Configure(IWebHostBuilder builder)
    //    {
    //        builder.ConfigureServices(services =>
    //        {
    //            services.AddSmidge(Configuration.GetSection("smidge"));
    //        });
    //    }
    //}

    public static class SmidgeStartup
    {
        private static ConcurrentBag<string> _mappedRoutes = new ConcurrentBag<string>();

        public static IServiceCollection AddSmidge(this IServiceCollection services, 
            IConfiguration smidgeConfiguration = null, 
            IFileProvider fileProvider = null)
        {            
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.TryAddSingleton<IActionContextAccessor, ActionContextAccessor>();
            
            services.AddTransient<IConfigureOptions<SmidgeOptions>, SmidgeOptionsSetup>();

            services.AddSingleton<PreProcessManager>();
            services.AddSingleton<IRequestHelper, RequestHelper>();
            services.AddSingleton<IWebsiteInfo, AutoWebsiteInfo>();
            services.AddSingleton<IBundleFileSetGenerator, BundleFileSetGenerator>();
            services.AddSingleton<IHasher, Crc32Hasher>();
            services.AddSingleton<IBundleManager, BundleManager>();
            services.AddSingleton<PreProcessPipelineFactory>();
            services.AddSingleton<FileSystemHelper>(p =>
            {
                var hosting = p.GetRequiredService<IHostingEnvironment>();
                var provider = fileProvider ?? hosting.WebRootFileProvider;
                return new FileSystemHelper(hosting, p.GetRequiredService<ISmidgeConfig>(), provider, p.GetRequiredService<IHasher>());
            });            
            services.AddSingleton<ISmidgeConfig>((p) =>
            {
                if (smidgeConfiguration == null)
                {
                    return new SmidgeConfig(p.GetRequiredService<IHostingEnvironment>());
                }
                return new SmidgeConfig(smidgeConfiguration);
            });
            
            services.AddSingleton<ICacheBuster, ConfigCacheBuster>();
            services.AddSingleton<ICacheBuster, AppDomainLifetimeCacheBuster>();
            services.AddSingleton<CacheBusterResolver>();

            //These all execute as part of the request/scope            
            services.AddScoped<DynamicallyRegisteredWebFiles>();
            services.AddScoped<SmidgeHelper>();
            services.AddScoped<IUrlManager, DefaultUrlManager>();            

            //pre-processors
            services.AddSingleton<IPreProcessor, JsMinifier>();
            services.AddSingleton<IPreProcessor, CssMinifier>();            
            services.AddSingleton<IPreProcessor, CssImportProcessor>();
            services.AddSingleton<IPreProcessor, CssUrlProcessor>();
            services.AddSingleton<Lazy<IEnumerable<IPreProcessor>>>(provider => new Lazy<IEnumerable<IPreProcessor>>(provider.GetRequiredService<IEnumerable<IPreProcessor>>));

            //conventions
            services.AddSingleton<FileProcessingConventions>();
            services.AddSingleton<IFileProcessingConvention, MinifiedFilePathConvention>();

            //Add the controller models as DI services - these get auto created for model binding
            services.AddTransient<BundleRequestModel>();
            services.AddTransient<CompositeFileModel>();

            return services;
        }
        
        public static void UseSmidge(this IApplicationBuilder app, Action<IBundleManager> configureBundles = null, SmidgeOptions smidgeOptions = null)
        {
            //Create custom route
            app.UseMvc(routes =>
            {
                var options = smidgeOptions ?? app.ApplicationServices.GetRequiredService<IOptions<SmidgeOptions>>().Value;

                var compositeRouteName = $"SmidgeComposite.{options.UrlOptions.CompositeFilePath}";
                if (!_mappedRoutes.TryPeek(out var _))
                {
                    _mappedRoutes.Add(compositeRouteName);
                    routes.MapRoute(
                        compositeRouteName,
                        options.UrlOptions.CompositeFilePath + "/{file}",
                        new { controller = "Smidge", action = "Composite" });
                }

                var bundleRouteName = $"SmidgeBundle.{options.UrlOptions.BundleFilePath}";
                if (!_mappedRoutes.TryPeek(out var _))
                {
                    _mappedRoutes.Add(compositeRouteName);
                    routes.MapRoute(
                        bundleRouteName,
                        options.UrlOptions.BundleFilePath + "/{bundle}",
                        new { controller = "Smidge", action = "Bundle" });
                }
                
            });

            if (configureBundles != null)
            {
                var bundleManager = app.ApplicationServices.GetRequiredService<IBundleManager>();
                configureBundles(bundleManager);

                var cacheBusterResolver = app.ApplicationServices.GetRequiredService<CacheBusterResolver>();

                //TODO: Now that they are configured we need to wire up the file watching event handlers
                // to the bundle manager, currently these are on the Bundle, but that is not good enough
                // since we need the bundle name
                foreach (var webFileType in new[] {WebFileType.Css, WebFileType.Js })
                {
                    var names = bundleManager.GetBundleNames(webFileType);
                    foreach (var cssName in names)
                    {
                        var bundle = bundleManager.GetBundle(cssName);
                        WireUpFileWatchEventHandlers(bundleManager, cacheBusterResolver, cssName, bundle);
                    }
                }
            }    

        }

        private static void WireUpFileWatchEventHandlers(IBundleManager bundleManager, CacheBusterResolver cacheBusterResolver, string bundleName, Bundle bundle)
        {
            if (bundle.BundleOptions == null) return;

            if (bundle.BundleOptions.DebugOptions.FileWatchOptions.Enabled)
            {
                bundle.BundleOptions.DebugOptions.FileWatchOptions.FileModified += FileWatchOptions_FileModified(bundleManager, cacheBusterResolver, bundleName, bundle, true);
            }
            if (bundle.BundleOptions.ProductionOptions.FileWatchOptions.Enabled)
            {
                bundle.BundleOptions.ProductionOptions.FileWatchOptions.FileModified += FileWatchOptions_FileModified(bundleManager, cacheBusterResolver, bundleName, bundle, false);
            }
        }

        private static EventHandler<FileWatchEventArgs> FileWatchOptions_FileModified(IBundleManager bundleManager, CacheBusterResolver cacheBusterResolver, string bundleName, Bundle bundle, bool debug)
        {
            return (sender, args) =>
            {
                FileWatchOptions_FileModified(bundleManager, cacheBusterResolver, bundleName, bundle, debug, args);
            };
        }

        private static void FileWatchOptions_FileModified(IBundleManager bundleManager, CacheBusterResolver cacheBusterResolver, string bundleName, Bundle bundle, bool debug, FileWatchEventArgs e)
        {
            var cacheBuster = cacheBusterResolver.GetCacheBuster(bundle.GetBundleOptions(bundleManager, debug).GetCacheBusterType());                

            //this file is part of this bundle, so the persisted processed/combined/compressed  will need to be 
            // invalidated/deleted/renamed
            foreach (var compressionType in new[] { CompressionType.deflate, CompressionType.gzip, CompressionType.none })
            {
                var compFilePath = e.FileSystemHelper.GetCompositeFileInfo(cacheBuster, compressionType, bundleName);
                if (File.Exists(compFilePath))
                {
                    File.Delete(compFilePath);
                }
            }
        }
    }
}
