﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Smidge.Cache;
using Smidge.FileProcessors;
using Smidge.Models;

namespace Smidge.CompositeFiles
{
    /// <summary>
    /// Tracks state for a bundle of files and is used to combine files into one
    /// </summary>
    public class BundleContext : IDisposable
    {
        

        /// <summary>
        /// Creates an empty <see cref="BundleContext"/> which does not track prependers or appenders
        /// </summary>
        /// <returns></returns>
        public static BundleContext CreateEmpty()
        {
            return new BundleContext();
        }

        private BundleContext()
        {
        }

        public BundleContext(IRequestModel bundleRequest, IFileInfo bundleCompositeFile)
        {
            BundleRequest = bundleRequest;
            _bundleCompositeFile = bundleCompositeFile;
        }

        private readonly List<Func<string>> _appenders = new List<Func<string>>();
        private readonly List<Func<string>> _prependers = new List<Func<string>>();        
        private readonly IFileInfo _bundleCompositeFile;

        public IRequestModel BundleRequest { get; }
        /// <summary>
        /// Allows for any <see cref="IPreProcessor"/> to track state among the collection of files
        /// </summary>
        public IDictionary<string, object> Items { get; private set; } = new Dictionary<string, object>();

        /// <summary>
        /// Returns the FileInfo instance of the composite bundle file
        /// </summary>
        public IFileInfo BundleCompositeFile
        {
            get
            {
                if (_bundleCompositeFile == null) throw new NotSupportedException("No file available in an empty " + nameof(BundleContext));
                return _bundleCompositeFile;
            }
        }


        /// <summary>
        /// Returns the bundle file name
        /// </summary>
        /// <remarks>
        /// If it's an empty bundle context (i.e. it's not processing a real bundle but only a composite file) then this will be generated
        /// </remarks>
        public string BundleName => BundleRequest?.FileKey ?? "generated_" + Guid.NewGuid();

        public string FileExtension => BundleRequest?.Extension ?? string.Empty;

        public void AddAppender(Func<string> appender)
        {
            if (_bundleCompositeFile == null) return;
            _appenders.Add(appender);
        }

        public void AddPrepender(Func<string> prepender)
        {
            if (_bundleCompositeFile == null) return;
            _prependers.Add(prepender);
        }

        /// <summary>
        /// Combines streams into a single stream
        /// </summary>
        /// <param name="inputs"></param>
        /// <returns></returns>
        /// <remarks>
        /// This does not dispose the input streams
        /// </remarks>
        public async Task<Stream> GetCombinedStreamAsync(IEnumerable<Stream> inputs)
        {
            //TODO: Should we use a buffer pool here?

            var semicolon = Encoding.UTF8.GetBytes(";");
            var ms = new MemoryStream();
            //prependers
            foreach (var prepender in _prependers)
            {
                var bytes = Encoding.UTF8.GetBytes(prepender());
                await ms.WriteAsync(bytes, 0, bytes.Length);
            }
            
            //files
            foreach (var input in inputs)
            {
                await input.CopyToAsync(ms);
                await ms.WriteAsync(semicolon, 0, semicolon.Length);
            }

            //prependers
            foreach (var appender in _appenders)
            {
                var bytes = Encoding.UTF8.GetBytes(appender());
                await ms.WriteAsync(bytes, 0, bytes.Length);
            }

            //ensure it's reset
            ms.Position = 0;
            return ms;
        }

        public void Dispose()
        {
            Items?.Clear();
            _appenders?.Clear();
            _prependers?.Clear();
            Items = null;
        }
    }
}
