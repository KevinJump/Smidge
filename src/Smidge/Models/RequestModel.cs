﻿using Smidge.CompositeFiles;
using System;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Smidge.Cache;

namespace Smidge.Models
{
    /// <summary>
    /// Base class model for an inbound request
    /// </summary>
    public abstract class RequestModel : IRequestModel
    {
        protected RequestModel(string valueName, IUrlManager urlManager, IActionContextAccessor accessor, IRequestHelper requestHelper)
        {
            //default 
            LastFileWriteTime = DateTime.Now;

            Compression = requestHelper.GetClientCompression(accessor.ActionContext.HttpContext.Request.Headers);

            var bundleId = (string)accessor.ActionContext.RouteData.Values[valueName];
            ParsedPath = urlManager.ParsePath(bundleId);
            Debug = ParsedPath.Debug;

            switch (ParsedPath.WebType)
            {
                case WebFileType.Js:
                    Extension = ".js";
                    Mime = "text/javascript";
                    break;
                case WebFileType.Css:
                default:
                    Extension = ".css";
                    Mime = "text/css";
                    break;
            }
        }

        /// <summary>
        /// The cache buster for the current file request
        /// </summary>
        public abstract ICacheBuster CacheBuster { get; }

        /// <summary>
        /// The bundle definition name - this is either the bundle name when using named bundles or the composite file
        /// key generated when using composite files
        /// </summary>
        public abstract string FileKey { get; }

        public bool Debug { get; }
        public ParsedUrlPath ParsedPath { get; }

        /// <summary>
        /// The compression type allowed by the client/browser for this request
        /// </summary>
        public CompressionType Compression { get; private set; }

        public string Extension { get; private set; }
        public string Mime { get; private set; }

        public DateTimeOffset LastFileWriteTime { get; set; }
    }
}