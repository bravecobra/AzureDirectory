﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Store;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Directory = Lucene.Net.Store.Directory;

namespace AzureDirectory.Core {
    public class AzureDirectory : Directory {
        private readonly string _containerName;
        private readonly string _rootFolder;
        private CloudBlobClient _blobClient;
        private CloudBlobContainer _blobContainer;
        private Directory _cacheDirectory;


        /// <summary>
        /// Create an AzureDirectory
        /// </summary>
        /// <param name="storageAccount">storage account to use</param>
        /// <param name="containerName">name of container (folder in blob storage)</param>
        /// <param name="cacheDirectory">local Directory object to use for local cache</param>
        /// <param name="compressBlobs"></param>
        /// <param name="rootFolder">path of the root folder inside the container</param>
        public AzureDirectory(
            CloudStorageAccount storageAccount,
            string containerName = null,
            Directory cacheDirectory = null,
            bool compressBlobs = false,
            string rootFolder = null) {
            if (storageAccount == null)
                throw new ArgumentNullException(nameof(storageAccount));

            _containerName = string.IsNullOrEmpty(containerName) ? "lucene" : containerName.ToLower();


            if (string.IsNullOrEmpty(rootFolder))
                _rootFolder = string.Empty;
            else {
                rootFolder = rootFolder.Trim('/');
                _rootFolder = rootFolder + "/";
            }


            _blobClient = storageAccount.CreateCloudBlobClient();
            _initCacheDirectory(cacheDirectory);
            CompressBlobs = compressBlobs;
        }

        public CloudBlobContainer BlobContainer => _blobContainer;

        public bool CompressBlobs {
            get;
            set;
        }

        public Directory CacheDirectory {
            get => _cacheDirectory;
            set => _cacheDirectory = value;
        }

        private void _initCacheDirectory(Directory cacheDirectory) {
            if (cacheDirectory != null) {
                // save it off
                _cacheDirectory = cacheDirectory;
            }
            else {
                var cachePath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "AzureDirectory");
                var azureDir = new DirectoryInfo(cachePath);
                if (!azureDir.Exists)
                    azureDir.Create();

                var catalogPath = Path.Combine(cachePath, _containerName);
                var catalogDir = new DirectoryInfo(catalogPath);
                if (!catalogDir.Exists)
                    catalogDir.Create();

                _cacheDirectory = FSDirectory.Open(catalogPath);
            }

            CreateContainer();
        }

        public void CreateContainer() {
            _blobContainer = _blobClient.GetContainerReference(_containerName);
            _blobContainer.CreateIfNotExists();
        }

        /// <summary>Returns an array of strings, one for each file in the directory. </summary>
        public override string[] ListAll() {
            var results = from blob in _blobContainer.ListBlobs(_rootFolder)
                          select blob.Uri.AbsolutePath.Substring(blob.Uri.AbsolutePath.LastIndexOf('/') + 1);
            return results.ToArray();
        }

        /// <summary>Returns true if a file with the given name exists. </summary>
        [Obsolete("this method will be removed in 5.0")]
        public override bool FileExists(string name) {
            // this always comes from the server
            try {
                return _blobContainer.GetBlockBlobReference(_rootFolder + name).Exists();
            }
            catch (Exception) {
                return false;
            }
        }

        /// <summary>Returns the time the named file was last modified. </summary>
        public long FileModified(string name) {
            // this always has to come from the server
            try {
                var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
                blob.FetchAttributes();
                return blob.Properties.LastModified?.UtcDateTime.ToFileTimeUtc() ?? 0;
            }
            catch {
                return 0;
            }
        }

        /// <summary>Set the modified time of an existing file to now. </summary>
        public void TouchFile(string name) {
            //BlobProperties props = _blobContainer.GetBlobProperties(_rootFolder + name);
            //_blobContainer.UpdateBlobMetadata(props);
            // I have no idea what the semantics of this should be...hmmmm...
            // we never seem to get called
            //_cacheDirectory.TouchFile(name);
            //SetCachedBlobProperties(props);
        }

        /// <summary>Removes an existing file in the directory. </summary>
        public override void DeleteFile(string name) {
            // We're going to try to remove this from the cache directory first,
            // because the IndexFileDeleter will call this file to remove files 
            // but since some files will be in use still, it will retry when a reader/searcher
            // is refreshed until the file is no longer locked. So we need to try to remove 
            // from local storage first and if it fails, let it keep throwing the IOException
            // since that is what Lucene is expecting in order for it to retry.
            // If we remove the main storage file first, then this will never retry to clean out
            // local storage because the FileExist method will always return false.
            _cacheDirectory.DeleteFile(name + ".blob");
            _cacheDirectory.DeleteFile(name);

            //if we've made it this far then the cache directly file has been successfully removed so now we'll do the master

            var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
            blob.DeleteIfExists();
        }


        /// <summary>Returns the length of a file in the directory. </summary>
        public override long FileLength(string name) {
            var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
            blob.FetchAttributes();

            // index files may be compressed so the actual length is stored in metadata
            var hasMetadataValue = blob.Metadata.TryGetValue("CachedLength", out var blobLegthMetadata);

            if (hasMetadataValue && long.TryParse(blobLegthMetadata, out var blobLength)) {
                return blobLength;
            }
            return blob.Properties.Length; // fall back to actual blob size
        }

        public override IndexOutput CreateOutput(string name, IOContext context) {
            var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
            return new AzureIndexOutput(this, blob);
        }

        public override void Sync(ICollection<string> names) {
            // TODO: Figure out what to do here
        }

        public override IndexInput OpenInput(string name, IOContext context) {
            try {
                var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
                blob.FetchAttributes();
                return new AzureIndexInput("someToken", this, blob); // TODO: replace someToken
            }
            catch (Exception err) {
                throw new FileNotFoundException(name, err);
            }
        }

        /// <summary>Creates a new, empty file in the directory with the given name.
        /// Returns a stream writing this file. 
        /// </summary>
        public IndexOutput CreateOutput(string name) {
            var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
            return new AzureIndexOutput(this, blob);
        }

        /// <summary>Returns a stream reading an existing file. </summary>
        public IndexInput OpenInput(string name) {
            try {
                var blob = _blobContainer.GetBlockBlobReference(_rootFolder + name);
                blob.FetchAttributes();
                return new AzureIndexInput("someToken", this, blob); // TODO: replace someToken
            }
            catch (Exception err) {
                throw new FileNotFoundException(name, err);
            }
        }

        private readonly Dictionary<string, AzureLock> _locks = new Dictionary<string, AzureLock>();

        /// <summary>Construct a {@link Lock}.</summary>
        /// <param name="name">the name of the lock file
        /// </param>
        public override Lock MakeLock(string name) {
            lock (_locks) {
                if (!_locks.ContainsKey(name)) {
                    _locks.Add(name, new AzureLock(_rootFolder + name, this));
                }
                return _locks[name];
            }
        }

        public override void ClearLock(string name) {
            lock (_locks) {
                if (_locks.ContainsKey(name)) {
                    _locks[name].BreakLock();
                }
            }
            _cacheDirectory.ClearLock(name);
        }

        /// <summary>Closes the store. </summary>
        protected override void Dispose(bool disposing) {
            _blobContainer = null;
            _blobClient = null;
        }

        public override void SetLockFactory(LockFactory lockFactory) {
            throw new NotImplementedException();
        }

        public override LockFactory LockFactory => new NativeFSLockFactory();

        public virtual bool ShouldCompressFile(string path) {
            if (!CompressBlobs)
                return false;

            var ext = Path.GetExtension(path);
            switch (ext) {
                case ".cfs":
                case ".fdt":
                case ".fdx":
                case ".frq":
                case ".tis":
                case ".tii":
                case ".nrm":
                case ".tvx":
                case ".tvd":
                case ".tvf":
                case ".prx":
                    return true;
                default:
                    return false;
            }
        }

        public StreamOutput CreateCachedOutputAsStream(string name) {
            return new StreamOutput(CacheDirectory.CreateOutput(name, new IOContext(IOContext.UsageContext.DEFAULT)));
        }

    }

}
