// -----------------------------------------------------------------------
//  <copyright file="DiskFullException.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System.IO;
using Sparrow.Utils;

namespace Voron.Exceptions
{
    public class DiskFullException : IOException
    {
        public DiskFullException()
        {
        }

        public string DirectoryPath;
        public long CurrentFreeSpace;

        public DiskFullException(string filePath, long requestedFileSize, long? freeSpace)
            : base(
                $"There is not enough space to set the size of file {filePath} to {Sizes.Humane(requestedFileSize)}. " +
                $"Currently available space: {Sizes.Humane(freeSpace) ?? "N/A"}"
            )
        {
            DirectoryPath = Path.GetDirectoryName(filePath);
            CurrentFreeSpace = freeSpace ?? requestedFileSize - 1;
        }

        public DiskFullException(string message) : base (message)
        {

        }
    }
}
