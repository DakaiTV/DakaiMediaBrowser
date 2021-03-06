﻿using Emby.Drawing.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Drawing
{
    /// <summary>
    /// Class ImageProcessor
    /// </summary>
    public class ImageProcessor : IImageProcessor, IDisposable
    {
        /// <summary>
        /// The us culture
        /// </summary>
        protected readonly CultureInfo UsCulture = new CultureInfo("en-US");

        /// <summary>
        /// The _cached imaged sizes
        /// </summary>
        private readonly ConcurrentDictionary<Guid, ImageSize> _cachedImagedSizes;

        /// <summary>
        /// Gets the list of currently registered image processors
        /// Image processors are specialized metadata providers that run after the normal ones
        /// </summary>
        /// <value>The image enhancers.</value>
        public IEnumerable<IImageEnhancer> ImageEnhancers { get; private set; }

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IServerApplicationPaths _appPaths;
        private readonly IImageEncoder _imageEncoder;

        public ImageProcessor(ILogger logger, IServerApplicationPaths appPaths, IFileSystem fileSystem, IJsonSerializer jsonSerializer, IImageEncoder imageEncoder)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;
            _imageEncoder = imageEncoder;
            _appPaths = appPaths;

            ImageEnhancers = new List<IImageEnhancer>();
            _saveImageSizeTimer = new Timer(SaveImageSizeCallback, null, Timeout.Infinite, Timeout.Infinite);

            Dictionary<Guid, ImageSize> sizeDictionary;

            try
            {
                sizeDictionary = jsonSerializer.DeserializeFromFile<Dictionary<Guid, ImageSize>>(ImageSizeFile) ??
                    new Dictionary<Guid, ImageSize>();
            }
            catch (FileNotFoundException)
            {
                // No biggie
                sizeDictionary = new Dictionary<Guid, ImageSize>();
            }
            catch (DirectoryNotFoundException)
            {
                // No biggie
                sizeDictionary = new Dictionary<Guid, ImageSize>();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Error parsing image size cache file", ex);

                sizeDictionary = new Dictionary<Guid, ImageSize>();
            }

            _cachedImagedSizes = new ConcurrentDictionary<Guid, ImageSize>(sizeDictionary);
        }

        public string[] SupportedInputFormats
        {
            get
            {
                return _imageEncoder.SupportedInputFormats;
            }
        }

        private string ResizedImageCachePath
        {
            get
            {
                return Path.Combine(_appPaths.ImageCachePath, "resized-images");
            }
        }

        private string EnhancedImageCachePath
        {
            get
            {
                return Path.Combine(_appPaths.ImageCachePath, "enhanced-images");
            }
        }

        private string CroppedWhitespaceImageCachePath
        {
            get
            {
                return Path.Combine(_appPaths.ImageCachePath, "cropped-images");
            }
        }

        public void AddParts(IEnumerable<IImageEnhancer> enhancers)
        {
            ImageEnhancers = enhancers.ToArray();
        }

        public async Task ProcessImage(ImageProcessingOptions options, Stream toStream)
        {
            var file = await ProcessImage(options).ConfigureAwait(false);

            using (var fileStream = _fileSystem.GetFileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, true))
            {
                await fileStream.CopyToAsync(toStream).ConfigureAwait(false);
            }
        }

        public ImageFormat[] GetSupportedImageOutputFormats()
        {
            return _imageEncoder.SupportedOutputFormats;
        }

        public async Task<string> ProcessImage(ImageProcessingOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            var originalImagePath = options.Image.Path;

            if (options.HasDefaultOptions(originalImagePath) && options.Enhancers.Count == 0 && !options.CropWhiteSpace)
            {
                // Just spit out the original file if all the options are default
                return originalImagePath;
            }

            var dateModified = options.Image.DateModified;

            if (options.CropWhiteSpace)
            {
                var tuple = await GetWhitespaceCroppedImage(originalImagePath, dateModified).ConfigureAwait(false);

                originalImagePath = tuple.Item1;
                dateModified = tuple.Item2;
            }

            if (options.Enhancers.Count > 0)
            {
                var tuple = await GetEnhancedImage(new ItemImageInfo
                {
                    DateModified = dateModified,
                    Type = options.Image.Type,
                    Path = originalImagePath

                }, options.Item, options.ImageIndex, options.Enhancers).ConfigureAwait(false);

                originalImagePath = tuple.Item1;
                dateModified = tuple.Item2;
            }

            var originalImageSize = GetImageSize(originalImagePath, dateModified);

            // Determine the output size based on incoming parameters
            var newSize = DrawingUtils.Resize(originalImageSize, options.Width, options.Height, options.MaxWidth, options.MaxHeight);

            if (options.HasDefaultOptionsWithoutSize(originalImagePath) && newSize.Equals(originalImageSize) && options.Enhancers.Count == 0)
            {
                // Just spit out the original file if the new size equals the old
                return originalImagePath;
            }

            var quality = options.Quality ?? 90;

            var outputFormat = GetOutputFormat(options.OutputFormat);
            var cacheFilePath = GetCacheFilePath(originalImagePath, newSize, quality, dateModified, outputFormat, options.AddPlayedIndicator, options.PercentPlayed, options.UnplayedCount, options.BackgroundColor);

            var semaphore = GetLock(cacheFilePath);

            await semaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                CheckDisposed();

                if (!File.Exists(cacheFilePath))
                {
                    var newWidth = Convert.ToInt32(newSize.Width);
                    var newHeight = Convert.ToInt32(newSize.Height);

                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath));

                    _imageEncoder.EncodeImage(originalImagePath, cacheFilePath, newWidth, newHeight, quality, options);
                }
            }
            finally
            {
                semaphore.Release();
            }

            return cacheFilePath;
        }

        private ImageFormat GetOutputFormat(ImageFormat requestedFormat)
        {
            if (requestedFormat == ImageFormat.Webp && !_imageEncoder.SupportedOutputFormats.Contains(ImageFormat.Webp))
            {
                return ImageFormat.Png;
            }

            return requestedFormat;
        }

        /// <summary>
        /// Crops whitespace from an image, caches the result, and returns the cached path
        /// </summary>
        private async Task<Tuple<string, DateTime>> GetWhitespaceCroppedImage(string originalImagePath, DateTime dateModified)
        {
            var name = originalImagePath;
            name += "datemodified=" + dateModified.Ticks;

            var croppedImagePath = GetCachePath(CroppedWhitespaceImageCachePath, name, Path.GetExtension(originalImagePath));

            var semaphore = GetLock(croppedImagePath);

            await semaphore.WaitAsync().ConfigureAwait(false);

            // Check again in case of contention
            if (File.Exists(croppedImagePath))
            {
                semaphore.Release();
                return GetResult(croppedImagePath);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(croppedImagePath));

                _imageEncoder.CropWhiteSpace(originalImagePath, croppedImagePath);
            }
            catch (Exception ex)
            {
                // We have to have a catch-all here because some of the .net image methods throw a plain old Exception
                _logger.ErrorException("Error cropping image {0}", ex, originalImagePath);

                return new Tuple<string, DateTime>(originalImagePath, dateModified);
            }
            finally
            {
                semaphore.Release();
            }

            return GetResult(croppedImagePath);
        }

        private Tuple<string, DateTime> GetResult(string path)
        {
            return new Tuple<string, DateTime>(path, _fileSystem.GetLastWriteTimeUtc(path));
        }

        /// <summary>
        /// Increment this when there's a change requiring caches to be invalidated
        /// </summary>
        private const string Version = "3";

        /// <summary>
        /// Gets the cache file path based on a set of parameters
        /// </summary>
        private string GetCacheFilePath(string originalPath, ImageSize outputSize, int quality, DateTime dateModified, ImageFormat format, bool addPlayedIndicator, double percentPlayed, int? unwatchedCount, string backgroundColor)
        {
            var filename = originalPath;

            filename += "width=" + outputSize.Width;

            filename += "height=" + outputSize.Height;

            filename += "quality=" + quality;

            filename += "datemodified=" + dateModified.Ticks;

            filename += "f=" + format;

            if (addPlayedIndicator)
            {
                filename += "pl=true";
            }

            if (percentPlayed > 0)
            {
                filename += "p=" + percentPlayed;
            }

            if (unwatchedCount.HasValue)
            {
                filename += "p=" + unwatchedCount.Value;
            }

            if (!string.IsNullOrEmpty(backgroundColor))
            {
                filename += "b=" + backgroundColor;
            }

            filename += "v=" + Version;

            return GetCachePath(ResizedImageCachePath, filename, "." + format.ToString().ToLower());
        }

        /// <summary>
        /// Gets the size of the image.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>ImageSize.</returns>
        public ImageSize GetImageSize(string path)
        {
            return GetImageSize(path, File.GetLastWriteTimeUtc(path));
        }

        public ImageSize GetImageSize(ItemImageInfo info)
        {
            return GetImageSize(info.Path, info.DateModified);
        }

        /// <summary>
        /// Gets the size of the image.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="imageDateModified">The image date modified.</param>
        /// <returns>ImageSize.</returns>
        /// <exception cref="System.ArgumentNullException">path</exception>
        private ImageSize GetImageSize(string path, DateTime imageDateModified)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            var name = path + "datemodified=" + imageDateModified.Ticks;

            ImageSize size;

            var cacheHash = name.GetMD5();

            if (!_cachedImagedSizes.TryGetValue(cacheHash, out size))
            {
                size = GetImageSizeInternal(path);

                _cachedImagedSizes.AddOrUpdate(cacheHash, size, (keyName, oldValue) => size);
            }

            return size;
        }

        /// <summary>
        /// Gets the image size internal.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>ImageSize.</returns>
        private ImageSize GetImageSizeInternal(string path)
        {
            ImageSize size;

            try
            {
                size = ImageHeader.GetDimensions(path, _logger, _fileSystem);
            }
            catch
            {
                _logger.Info("Failed to read image header for {0}. Doing it the slow way.", path);

                CheckDisposed();

                size = _imageEncoder.GetImageSize(path);
            }

            StartSaveImageSizeTimer();

            return size;
        }

        private readonly Timer _saveImageSizeTimer;
        private const int SaveImageSizeTimeout = 5000;
        private readonly object _saveImageSizeLock = new object();
        private void StartSaveImageSizeTimer()
        {
            _saveImageSizeTimer.Change(SaveImageSizeTimeout, Timeout.Infinite);
        }

        private void SaveImageSizeCallback(object state)
        {
            lock (_saveImageSizeLock)
            {
                try
                {
                    var path = ImageSizeFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    _jsonSerializer.SerializeToFile(_cachedImagedSizes, path);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error saving image size file", ex);
                }
            }
        }

        private string ImageSizeFile
        {
            get
            {
                return Path.Combine(_appPaths.DataPath, "imagesizes.json");
            }
        }

        /// <summary>
        /// Gets the image cache tag.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="image">The image.</param>
        /// <returns>Guid.</returns>
        /// <exception cref="System.ArgumentNullException">item</exception>
        public string GetImageCacheTag(IHasImages item, ItemImageInfo image)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            var supportedEnhancers = GetSupportedEnhancers(item, image.Type);

            return GetImageCacheTag(item, image, supportedEnhancers.ToList());
        }

        /// <summary>
        /// Gets the image cache tag.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="image">The image.</param>
        /// <param name="imageEnhancers">The image enhancers.</param>
        /// <returns>Guid.</returns>
        /// <exception cref="System.ArgumentNullException">item</exception>
        public string GetImageCacheTag(IHasImages item, ItemImageInfo image, List<IImageEnhancer> imageEnhancers)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            if (imageEnhancers == null)
            {
                throw new ArgumentNullException("imageEnhancers");
            }

            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            var originalImagePath = image.Path;
            var dateModified = image.DateModified;
            var imageType = image.Type;

            // Optimization
            if (imageEnhancers.Count == 0)
            {
                return (originalImagePath + dateModified.Ticks).GetMD5().ToString("N");
            }

            // Cache name is created with supported enhancers combined with the last config change so we pick up new config changes
            var cacheKeys = imageEnhancers.Select(i => i.GetConfigurationCacheKey(item, imageType)).ToList();
            cacheKeys.Add(originalImagePath + dateModified.Ticks);

            return string.Join("|", cacheKeys.ToArray()).GetMD5().ToString("N");
        }

        /// <summary>
        /// Gets the enhanced image.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns>Task{System.String}.</returns>
        public async Task<string> GetEnhancedImage(IHasImages item, ImageType imageType, int imageIndex)
        {
            var enhancers = GetSupportedEnhancers(item, imageType).ToList();

            var imageInfo = item.GetImageInfo(imageType, imageIndex);

            var result = await GetEnhancedImage(imageInfo, item, imageIndex, enhancers);

            return result.Item1;
        }

        private async Task<Tuple<string, DateTime>> GetEnhancedImage(ItemImageInfo image,
            IHasImages item,
            int imageIndex,
            List<IImageEnhancer> enhancers)
        {
            var originalImagePath = image.Path;
            var dateModified = image.DateModified;
            var imageType = image.Type;

            try
            {
                var cacheGuid = GetImageCacheTag(item, image, enhancers);

                // Enhance if we have enhancers
                var ehnancedImagePath = await GetEnhancedImageInternal(originalImagePath, item, imageType, imageIndex, enhancers, cacheGuid).ConfigureAwait(false);

                // If the path changed update dateModified
                if (!ehnancedImagePath.Equals(originalImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    return GetResult(ehnancedImagePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error enhancing image", ex);
            }

            return new Tuple<string, DateTime>(originalImagePath, dateModified);
        }

        /// <summary>
        /// Gets the enhanced image internal.
        /// </summary>
        /// <param name="originalImagePath">The original image path.</param>
        /// <param name="item">The item.</param>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <param name="supportedEnhancers">The supported enhancers.</param>
        /// <param name="cacheGuid">The cache unique identifier.</param>
        /// <returns>Task&lt;System.String&gt;.</returns>
        /// <exception cref="ArgumentNullException">
        /// originalImagePath
        /// or
        /// item
        /// </exception>
        private async Task<string> GetEnhancedImageInternal(string originalImagePath,
            IHasImages item,
            ImageType imageType,
            int imageIndex,
            IEnumerable<IImageEnhancer> supportedEnhancers,
            string cacheGuid)
        {
            if (string.IsNullOrEmpty(originalImagePath))
            {
                throw new ArgumentNullException("originalImagePath");
            }

            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            // All enhanced images are saved as png to allow transparency
            var enhancedImagePath = GetCachePath(EnhancedImageCachePath, cacheGuid + ".png");

            var semaphore = GetLock(enhancedImagePath);

            await semaphore.WaitAsync().ConfigureAwait(false);

            // Check again in case of contention
            if (File.Exists(enhancedImagePath))
            {
                semaphore.Release();
                return enhancedImagePath;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(enhancedImagePath));
                await ExecuteImageEnhancers(supportedEnhancers, originalImagePath, enhancedImagePath, item, imageType, imageIndex).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }

            return enhancedImagePath;
        }

        /// <summary>
        /// Executes the image enhancers.
        /// </summary>
        /// <param name="imageEnhancers">The image enhancers.</param>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="item">The item.</param>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns>Task{EnhancedImage}.</returns>
        private async Task ExecuteImageEnhancers(IEnumerable<IImageEnhancer> imageEnhancers, string inputPath, string outputPath, IHasImages item, ImageType imageType, int imageIndex)
        {
            // Run the enhancers sequentially in order of priority
            foreach (var enhancer in imageEnhancers)
            {
                var typeName = enhancer.GetType().Name;

                try
                {
                    await enhancer.EnhanceImageAsync(item, inputPath, outputPath, imageType, imageIndex).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("{0} failed enhancing {1}", ex, typeName, item.Name);

                    throw;
                }

                // Feed the output into the next enhancer as input
                inputPath = outputPath;
            }
        }

        /// <summary>
        /// The _semaphoreLocks
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphoreLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Gets the lock.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>System.Object.</returns>
        private SemaphoreSlim GetLock(string filename)
        {
            return _semaphoreLocks.GetOrAdd(filename, key => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// Gets the cache path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="uniqueName">Name of the unique.</param>
        /// <param name="fileExtension">The file extension.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// path
        /// or
        /// uniqueName
        /// or
        /// fileExtension
        /// </exception>
        public string GetCachePath(string path, string uniqueName, string fileExtension)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }
            if (string.IsNullOrEmpty(uniqueName))
            {
                throw new ArgumentNullException("uniqueName");
            }

            if (string.IsNullOrEmpty(fileExtension))
            {
                throw new ArgumentNullException("fileExtension");
            }

            var filename = uniqueName.GetMD5() + fileExtension;

            return GetCachePath(path, filename);
        }

        /// <summary>
        /// Gets the cache path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="filename">The filename.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// path
        /// or
        /// filename
        /// </exception>
        public string GetCachePath(string path, string filename)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }
            if (string.IsNullOrEmpty(filename))
            {
                throw new ArgumentNullException("filename");
            }

            var prefix = filename.Substring(0, 1);

            path = Path.Combine(path, prefix);

            return Path.Combine(path, filename);
        }

        public void CreateImageCollage(ImageCollageOptions options)
        {
            _imageEncoder.CreateImageCollage(options);
        }

        public IEnumerable<IImageEnhancer> GetSupportedEnhancers(IHasImages item, ImageType imageType)
        {
            return ImageEnhancers.Where(i =>
            {
                try
                {
                    return i.Supports(item, imageType);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in image enhancer: {0}", ex, i.GetType().Name);

                    return false;
                }

            });
        }

        private bool _disposed;
        public void Dispose()
        {
            _disposed = true;
            _imageEncoder.Dispose();
            _saveImageSizeTimer.Dispose();
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
