﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AaxDecrypter;
using AudibleApi;
using DataLayer;
using Dinah.Core;
using Dinah.Core.ErrorHandling;
using Dinah.Core.Net.Http;
using FileManager;

namespace FileLiberator
{
    public class DownloadDecryptBook : IAudioDecodable
    {
        private AaxcDownloadConverter aaxcDownloader;

        public event EventHandler<TimeSpan> StreamingTimeRemaining;
        public event EventHandler<Action<byte[]>> RequestCoverArt;
        public event EventHandler<string> TitleDiscovered;
        public event EventHandler<string> AuthorsDiscovered;
        public event EventHandler<string> NarratorsDiscovered;
        public event EventHandler<byte[]> CoverImageDiscovered;
        public event EventHandler<string> StreamingBegin;
        public event EventHandler<DownloadProgress> StreamingProgressChanged;
        public event EventHandler<string> StreamingCompleted;
        public event EventHandler<LibraryBook> Begin;
        public event EventHandler<string> StatusUpdate;
        public event EventHandler<LibraryBook> Completed;

        public DownloadDecryptBook()
        {
            RequestCoverArt += (o, e) => Serilog.Log.Logger.Debug("Event fired {@DebugInfo}", new { Name = nameof(RequestCoverArt) });
            TitleDiscovered += (o, e) => Serilog.Log.Logger.Debug("Event fired {@DebugInfo}", new { Name = nameof(TitleDiscovered), Title = e });
            AuthorsDiscovered += (o, e) => Serilog.Log.Logger.Debug("Event fired {@DebugInfo}", new { Name = nameof(AuthorsDiscovered), Authors = e });
            NarratorsDiscovered += (o, e) => Serilog.Log.Logger.Debug("Event fired {@DebugInfo}", new { Name = nameof(NarratorsDiscovered), Narrators = e });
            CoverImageDiscovered += (o, e) => Serilog.Log.Logger.Debug("Event fired {@DebugInfo}", new { Name = nameof(CoverImageDiscovered), CoverImageBytes = e?.Length });

            StreamingBegin += (o, e) => Serilog.Log.Logger.Information("Event fired {@DebugInfo}", new { Name = nameof(StreamingBegin), Message = e });
            StreamingCompleted += (o, e) => Serilog.Log.Logger.Information("Event fired {@DebugInfo}", new { Name = nameof(StreamingCompleted), Message = e });

            Begin += (o, e) => Serilog.Log.Logger.Information("Event fired {@DebugInfo}", new { Name = nameof(Begin), Book = e.LogFriendly() });
            Completed += (o, e) => Serilog.Log.Logger.Information("Event fired {@DebugInfo}", new { Name = nameof(Completed), Book = e.LogFriendly() });
        }

        public async Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
        {
            Begin?.Invoke(this, libraryBook);

            try
            {
                if (libraryBook.Book.Audio_Exists)
                    return new StatusHandler { "Cannot find decrypt. Final audio file already exists" };

                var outputAudioFilename = await aaxToM4bConverterDecryptAsync(AudibleFileStorage.DownloadsInProgress, AudibleFileStorage.DecryptInProgress, libraryBook);

                // decrypt failed
                if (outputAudioFilename is null)
                    return new StatusHandler { "Decrypt failed" };

                // moves files and returns dest dir
                var moveResults = MoveFilesToBooksDir(libraryBook.Book, outputAudioFilename);

                if (!moveResults.movedAudioFile)
                    return new StatusHandler { "Cannot find final audio file after decryption" };

                libraryBook.Book.UserDefinedItem.BookStatus = LiberatedStatus.Liberated;

                return new StatusHandler();
            }
            finally
            {
                Completed?.Invoke(this, libraryBook);
            }
        }

        private async Task<string> aaxToM4bConverterDecryptAsync(string cacheDir, string destinationDir, LibraryBook libraryBook)
        {
            StreamingBegin?.Invoke(this, $"Begin decrypting {libraryBook}");

            try
            {
                validate(libraryBook);

                var apiExtended = await InternalUtilities.ApiExtended.CreateAsync(libraryBook.Account, libraryBook.Book.Locale);

                var contentLic = await apiExtended.Api.GetDownloadLicenseAsync(libraryBook.Book.AudibleProductId);

                var aaxcDecryptDlLic = new DownloadLicense
                    (
                    contentLic?.ContentMetadata?.ContentUrl?.OfflineUrl,
                    contentLic?.Voucher?.Key,
                    contentLic?.Voucher?.Iv,
                    Resources.USER_AGENT
                    );

                if (Configuration.Instance.AllowLibationFixup)
                {
                    aaxcDecryptDlLic.ChapterInfo = new AAXClean.ChapterInfo();

                    foreach (var chap in contentLic.ContentMetadata?.ChapterInfo?.Chapters)
                        aaxcDecryptDlLic.ChapterInfo.AddChapter(chap.Title, TimeSpan.FromMilliseconds(chap.LengthMs));
                }


                var format = Configuration.Instance.DecryptToLossy ? OutputFormat.Mp3 : OutputFormat.Mp4a;

                var extension = format switch
                {
                    OutputFormat.Mp4a => "m4b",
                    OutputFormat.Mp3 => "mp3",
                    _ => throw new NotImplementedException(),
                };

                var outFileName = Path.Combine(destinationDir, $"{PathLib.ToPathSafeString(libraryBook.Book.Title)} [{libraryBook.Book.AudibleProductId}].{extension}");


                aaxcDownloader = new AaxcDownloadConverter(outFileName, cacheDir, aaxcDecryptDlLic, format) { AppName = "Libation" };
                aaxcDownloader.DecryptProgressUpdate += (s, progress) => StreamingProgressChanged?.Invoke(this, progress);
                aaxcDownloader.DecryptTimeRemaining += (s, remaining) => StreamingTimeRemaining?.Invoke(this, remaining);
                aaxcDownloader.RetrievedCoverArt += AaxcDownloader_RetrievedCoverArt;
                aaxcDownloader.RetrievedTags += aaxcDownloader_RetrievedTags;

                // REAL WORK DONE HERE
                var success = await Task.Run(() => aaxcDownloader.Run());

                // decrypt failed
                if (!success)
                    return null;

                return outFileName;
            }
            finally
            {
                StreamingCompleted?.Invoke(this, $"Completed downloading and decrypting {libraryBook.Book.Title}");
            }
        }


        private void AaxcDownloader_RetrievedCoverArt(object sender, byte[] e)
        {
            if (e is null && Configuration.Instance.AllowLibationFixup)
            {
                RequestCoverArt?.Invoke(this, aaxcDownloader.SetCoverArt);
            }

            if (e is not null)
            {
                CoverImageDiscovered?.Invoke(this, e);
            }
        }

        private void aaxcDownloader_RetrievedTags(object sender, AAXClean.AppleTags e)
        {
            TitleDiscovered?.Invoke(this, e.TitleSansUnabridged);
            AuthorsDiscovered?.Invoke(this, e.FirstAuthor ?? "[unknown]");
            NarratorsDiscovered?.Invoke(this, e.Narrator ?? "[unknown]");
        }

        private static (string destinationDir, bool movedAudioFile) MoveFilesToBooksDir(Book product, string outputAudioFilename)
        {
            // create final directory. move each file into it. MOVE AUDIO FILE LAST
            // new dir: safetitle_limit50char + " [" + productId + "]"

            var destinationDir = AudibleFileStorage.Audio.GetDestDir(product.Title, product.AudibleProductId);
            Directory.CreateDirectory(destinationDir);

            var sortedFiles = getProductFilesSorted(product, outputAudioFilename);

            var musicFileExt = Path.GetExtension(outputAudioFilename).Trim('.');

            // audio filename: safetitle_limit50char + " [" + productId + "]." + audio_ext
            var audioFileName = FileUtility.GetValidFilename(destinationDir, product.Title, musicFileExt, product.AudibleProductId);

            bool movedAudioFile = false;
            foreach (var f in sortedFiles)
            {
                var dest
                    = AudibleFileStorage.Audio.IsFileTypeMatch(f)
                    ? audioFileName
                    // non-audio filename: safetitle_limit50char + " [" + productId + "][" + audio_ext +"]." + non_audio_ext
                    : FileUtility.GetValidFilename(destinationDir, product.Title, f.Extension, product.AudibleProductId, musicFileExt);

                if (Path.GetExtension(dest).Trim('.').ToLower() == "cue")
                    Cue.UpdateFileName(f, audioFileName);

                File.Move(f.FullName, dest);

                movedAudioFile |= AudibleFileStorage.Audio.IsFileTypeMatch(f);
            }

            AudibleFileStorage.Audio.Refresh();

            return (destinationDir, movedAudioFile);
        }

        private static List<FileInfo> getProductFilesSorted(Book product, string outputAudioFilename)
        {
            // files are: temp path\author\[asin].ext
            var m4bDir = new FileInfo(outputAudioFilename).Directory;
            var files = m4bDir
                .EnumerateFiles()
                .Where(f => f.Name.ContainsInsensitive(product.AudibleProductId))
                .ToList();

            // move audio files to the end of the collection so these files are moved last
            var musicFiles = files.Where(f => AudibleFileStorage.Audio.IsFileTypeMatch(f));
            var sortedFiles = files
                .Except(musicFiles)
                .Concat(musicFiles)
                .ToList();

            return sortedFiles;
        }

        private static void validate(LibraryBook libraryBook)
        {
            string errorString(string field)
                => $"{errorTitle()}\r\nCannot download book. {field} is not known. Try re-importing the account which owns this book.";

            string errorTitle()
            {
                var title
                    = (libraryBook.Book.Title.Length > 53)
                    ? $"{libraryBook.Book.Title.Truncate(50)}..."
                    : libraryBook.Book.Title;
                var errorBookTitle = $"{title} [{libraryBook.Book.AudibleProductId}]";
                return errorBookTitle;
            };

            if (string.IsNullOrWhiteSpace(libraryBook.Account))
                throw new Exception(errorString("Account"));

            if (string.IsNullOrWhiteSpace(libraryBook.Book.Locale))
                throw new Exception(errorString("Locale"));
        }

        public bool Validate(LibraryBook libraryBook) => !libraryBook.Book.Audio_Exists;

        public void Cancel()
        {
            aaxcDownloader?.Cancel();
        }
    }
}
