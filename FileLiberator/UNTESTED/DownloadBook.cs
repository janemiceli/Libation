﻿using System;
using System.IO;
using System.Threading.Tasks;
using FileManager;
using DataLayer;
using Dinah.Core.ErrorHandling;

namespace FileLiberator
{
    /// <summary>
    /// Download DRM book and decrypt audiobook files.
    /// 
    /// Processes:
    /// Download: download aax file: the DRM encrypted audiobook
    /// Decrypt: remove DRM encryption from audiobook. Store final book
    /// Backup: perform all steps (downloaded, decrypt) still needed to get final book
    /// </summary>
    public class DownloadBook : DownloadableBase
    {
        public override async Task<bool> ValidateAsync(LibraryBook libraryBook)
            => !await AudibleFileStorage.Audio.ExistsAsync(libraryBook.Book.AudibleProductId)
            && !await AudibleFileStorage.AAX.ExistsAsync(libraryBook.Book.AudibleProductId);

        public override async Task<StatusHandler> ProcessItemAsync(LibraryBook libraryBook)
		{
			var tempAaxFilename = FileUtility.GetValidFilename(
				AudibleFileStorage.DownloadsInProgress,
				libraryBook.Book.Title,
				"aax",
				libraryBook.Book.AudibleProductId);

			// if getting from full title:
			// '?' is allowed
			// colons are inconsistent but not problematic to just leave them
			// - 1 colon: sometimes full title is used. sometimes only the part before the colon is used
			// - multple colons: only the part before the final colon is used
			//   e.g. Alien: Out of the Shadows: An Audible Original Drama => Alien: Out of the Shadows
			// in cases where title includes '&', just use everything before the '&' and ignore the rest
			//// var adhTitle = product.Title.Split('&')[0]

			// new/api method
			tempAaxFilename = await performApiDownloadAsync(libraryBook, tempAaxFilename);

			// move
			var aaxFilename = FileUtility.GetValidFilename(
				AudibleFileStorage.DownloadsFinal,
				libraryBook.Book.Title,
				"aax",
				libraryBook.Book.AudibleProductId);
			File.Move(tempAaxFilename, aaxFilename);

			var statusHandler = new StatusHandler();
			var isDownloaded = await AudibleFileStorage.AAX.ExistsAsync(libraryBook.Book.AudibleProductId);
			if (isDownloaded)
				Invoke_StatusUpdate($"Downloaded: {aaxFilename}");
			else
				statusHandler.AddError("Downloaded AAX file cannot be found");
			return statusHandler;
		}

		private async Task<string> performApiDownloadAsync(LibraryBook libraryBook, string tempAaxFilename)
		{
			var api = await AudibleApi.EzApiCreator.GetApiAsync(AudibleApiStorage.IdentityTokensFile);

			var progress = new Progress<Dinah.Core.Net.Http.DownloadProgress>();
			progress.ProgressChanged += (_, e) => Invoke_DownloadProgressChanged(this, e);

			Invoke_DownloadBegin(tempAaxFilename);
			var actualFilePath = await api.DownloadAaxWorkaroundAsync(libraryBook.Book.AudibleProductId, tempAaxFilename, progress);
			Invoke_DownloadCompleted(this, $"Completed: {actualFilePath}");

			return actualFilePath;
		}
	}
}