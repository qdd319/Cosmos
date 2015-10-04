﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cosmos.Common.Extensions;
using Cosmos.Debug.Kernel;
using Cosmos.HAL.BlockDevice;
using Cosmos.System.FileSystem.FAT.Listing;
using Cosmos.System.FileSystem.Listing;
using Directory = Cosmos.System.FileSystem.Listing.Directory;
using File = Cosmos.System.FileSystem.Listing.File;

namespace Cosmos.System.FileSystem.FAT
{
    public class FatFileSystem : FileSystem
    {
        readonly public UInt32 BytesPerSector;
        readonly public UInt32 SectorsPerCluster;
        readonly public UInt32 BytesPerCluster;

        readonly public UInt32 ReservedSectorCount;
        readonly public UInt32 TotalSectorCount;
        readonly public UInt32 ClusterCount;

        readonly public UInt32 NumberOfFATs;
        readonly public UInt32 FatSectorCount;

        readonly public UInt32 RootSector = 0; // FAT12/16
        readonly public UInt32 RootSectorCount = 0; // FAT12/16, FAT32 remains 0
        readonly public UInt32 RootCluster; // FAT32
        readonly public UInt32 RootEntryCount;

        readonly public UInt32 DataSector; // First Data Sector
        readonly public UInt32 DataSectorCount;

        public enum FatTypeEnum { Unknown, Fat12, Fat16, Fat32 }
        readonly public FatTypeEnum FatType = FatTypeEnum.Unknown;

        BlockDevice mDevice;

        public void ReadFatTableSector(UInt64 xSectorNum, byte[] aData)
        {
            mDevice.ReadBlock(ReservedSectorCount + xSectorNum, 1, aData);
        }

        public bool FatEntryIsEOF(UInt64 aValue)
        {
            if (FatType == FatTypeEnum.Fat12)
            {
                return aValue >= 0x0FF8;
            }
            else if (FatType == FatTypeEnum.Fat16)
            {
                return aValue >= 0xFFF8;
            }
            else
            {
                return aValue >= 0x0FFFFFF8;
            }
        }

        public UInt64 GetFatEntry(byte[] aSector, UInt64 aClusterNum, UInt64 aOffset)
        {
            if (FatType == FatTypeEnum.Fat12)
            {
                if (aOffset == (BytesPerSector - 1))
                {
                    throw new Exception("TODO: Sector Span");
                    /* This cluster access spans a sector boundary in the FAT */
                    /* There are a number of strategies to handling this. The */
                    /* easiest is to always load FAT sectors into memory */
                    /* in pairs if the volume is FAT12 (if you want to load */
                    /* FAT sector N, you also load FAT sector N+1 immediately */
                    /* following it in memory unless sector N is the last FAT */
                    /* sector). It is assumed that this is the strategy used here */
                    /* which makes this if test for a sector boundary span */
                    /* unnecessary. */
                }
                // We now access the FAT entry as a WORD just as we do for FAT16, but if the cluster number is
                // EVEN, we only want the low 12-bits of the 16-bits we fetch. If the cluster number is ODD
                // we want the high 12-bits of the 16-bits we fetch.
                UInt32 xResult = aSector.ToUInt16(aOffset);
                if ((aClusterNum & 0x01) == 0)
                { // Even
                    return xResult & 0x0FFF;
                }
                else
                { // Odd
                    return xResult >> 4;
                }
            }
            else if (FatType == FatTypeEnum.Fat16)
            {
                return aSector.ToUInt16(aOffset);
            }
            else
            {
                return aSector.ToUInt32(aOffset) & 0x0FFFFFFF;
            }
        }

        public FatFileSystem(BlockDevice aDevice)
        {

            mDevice = aDevice;
            byte[] xBPB = mDevice.NewBlockArray(1);

            mDevice.ReadBlock(0UL, 1U, xBPB);

            UInt16 xSig = xBPB.ToUInt16(510);
            if (xSig != 0xAA55)
            {
                throw new Exception("FAT signature not found.");
            }

            BytesPerSector = xBPB.ToUInt16(11);
            SectorsPerCluster = xBPB[13];
            BytesPerCluster = BytesPerSector * SectorsPerCluster;
            ReservedSectorCount = xBPB.ToUInt16(14);
            NumberOfFATs = xBPB[16];
            RootEntryCount = xBPB.ToUInt16(17);

            TotalSectorCount = xBPB.ToUInt16(19);
            if (TotalSectorCount == 0)
            {
                TotalSectorCount = xBPB.ToUInt32(32);
            }

            // FATSz
            FatSectorCount = xBPB.ToUInt16(22);
            if (FatSectorCount == 0)
            {
                FatSectorCount = xBPB.ToUInt32(36);
            }
            //Global.Dbg.Send("FAT Sector Count: " + FatSectorCount);

            DataSectorCount = TotalSectorCount - (ReservedSectorCount + (NumberOfFATs * FatSectorCount) + ReservedSectorCount);

            // Computation rounds down.
            ClusterCount = DataSectorCount / SectorsPerCluster;
            // Determine the FAT type. Do not use another method - this IS the official and
            // proper way to determine FAT type.
            // Comparisons are purposefully < and not <=
            // FAT16 starts at 4085, FAT32 starts at 65525
            if (ClusterCount < 4085)
            {
                FatType = FatTypeEnum.Fat12;
            }
            else if (ClusterCount < 65525)
            {
                FatType = FatTypeEnum.Fat16;
            }
            else
            {
                FatType = FatTypeEnum.Fat32;
            }

            if (FatType == FatTypeEnum.Fat32)
            {
                RootCluster = xBPB.ToUInt32(44);
            }
            else
            {
                RootSector = ReservedSectorCount + (NumberOfFATs * FatSectorCount);
                RootSectorCount = (RootEntryCount * 32 + (BytesPerSector - 1)) / BytesPerSector;
            }
            DataSector = ReservedSectorCount + (NumberOfFATs * FatSectorCount) + RootSectorCount;

        }

        public byte[] NewClusterArray()
        {
            return new byte[BytesPerCluster];
        }

        public void ReadCluster(UInt64 aCluster, byte[] aData)
        {
            UInt64 xSector = DataSector + ((aCluster - 2) * SectorsPerCluster);
            mDevice.ReadBlock(xSector, SectorsPerCluster, aData);
        }

        public void WriteCluster(UInt64 aCluster, byte[] aData)
        {
            UInt64 xSector = DataSector + ((aCluster - 2) * SectorsPerCluster);
            mDevice.WriteBlock(xSector, SectorsPerCluster, aData);
        }

        public void GetFatTableSector(UInt64 aClusterNum, out UInt32 oSector, out UInt32 oOffset)
        {
            UInt64 xOffset = 0;
            if (FatType == FatTypeEnum.Fat12)
            {
                // Multiply by 1.5 without using floating point, the divide by 2 rounds DOWN
                xOffset = aClusterNum + (aClusterNum / 2);
            }
            else if (FatType == FatTypeEnum.Fat16)
            {
                xOffset = aClusterNum * 2;
            }
            else if (FatType == FatTypeEnum.Fat32)
            {
                xOffset = aClusterNum * 4;
            }
            oSector = (UInt32)(xOffset / BytesPerSector);
            oOffset = (UInt32)(xOffset % BytesPerSector);
        }

        public List<Base> GetRoot()
        {
            byte[] xData;
            if (FatType == FatTypeEnum.Fat32)
            {
                xData = NewClusterArray();
                ReadCluster(RootCluster, xData);
            }
            else
            {
                xData = mDevice.NewBlockArray(RootSectorCount);
                mDevice.ReadBlock(RootSector, RootSectorCount, xData);
                // todo: is this correct??
            }
            return ReadDirectoryContents(xData, null);
        }

        public List<Base> GetDirectoryContents(FatDirectory directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException("directory");
            }

            byte[] xData;
            if (FatType == FatTypeEnum.Fat32)
            {
                xData = NewClusterArray();
                ReadCluster(directory.FirstClusterNr, xData);
            }
            else
            {
                xData = mDevice.NewBlockArray(1);
                mDevice.ReadBlock(directory.FirstClusterNr, RootSectorCount, xData);
            }
            // todo: what about larger directories?

            string xDirectoryPath;
            if (directory.BaseDirectory == null)
            {
                xDirectoryPath = directory.Name;
            }
            else
            {
                xDirectoryPath = directory.BaseDirectory + "\\" + directory.Name;
            }
            return ReadDirectoryContents(xData, xDirectoryPath);
        }

        private List<Base> ReadDirectoryContents(byte[] xData, string directoryPath)
        {
            var xResult = new List<Base>();
            //TODO: Change xLongName to StringBuilder
            string xLongName = "";
            string xName = "";
            for (UInt32 i = 0; i < xData.Length; i = i + 32)
            {
                FatHelpers.DevDebug("-------------------------------------------------");
                byte xAttrib = xData[i + 11];
                byte xStatus = xData[i];

                FatHelpers.DevDebug("Attrib = " + xAttrib.ToString() + ", Status = " + xStatus);
                if (xAttrib == DirectoryEntryAttributeConsts.LongName)
                {
                    byte xType = xData[i + 12];
                    byte xOrd = xData[i];
                    FatHelpers.DevDebug("Reading LFN with Seqnr " + xOrd.ToString() + ", Type = " + xType);
                    if (xOrd == 0xE5)
                    {
                        FatHelpers.DevDebug("Skipping deleted entry");
                        continue;
                    }
                    if (xType == 0)
                    {
                        if ((xOrd & 0x40) > 0)
                        {
                            xLongName = "";
                        }
                        //TODO: Check LDIR_Ord for ordering and throw exception
                        // if entries are found out of order.
                        // Also save buffer and only copy name if a end Ord marker is found.
                        string xLongPart = xData.GetUtf16String(i + 1, 5);
                        // We have to check the length because 0xFFFF is a valid Unicode codepoint.
                        // So we only want to stop if the 0xFFFF is AFTER a 0x0000. We can determin
                        // this by also looking at the length. Since we short circuit the or, the length
                        // is rarely evaluated.
                        if (xData.ToUInt16(i + 14) != 0xFFFF || xLongPart.Length == 5)
                        {
                            xLongPart = xLongPart + xData.GetUtf16String(i + 14, 6);
                            if (xData.ToUInt16(i + 28) != 0xFFFF || xLongPart.Length == 11)
                            {
                                xLongPart = xLongPart + xData.GetUtf16String(i + 28, 2);
                            }
                        }
                        xLongName = xLongPart + xLongName;
                        xLongPart = null;
                        //TODO: LDIR_Chksum
                    }
                }
                else
                {
                    xName = xLongName;
                    if (xStatus == 0x00)
                    {
                        // Empty slot, and no more entries after this
                        break;
                    }
                    else if (xStatus == 0x05)
                    {
                        // Japanese characters - We dont handle these
                    }
                    else if (xStatus == 0xE5)
                    {
                        // Empty slot, skip it
                    }
                    else if (xStatus >= 0x20)
                    {
                        if (xLongName.Length > 0)
                        {
                            // Leading and trailing spaces are to be ignored according to spec.
                            // Many programs (including Windows) pad trailing spaces although it
                            // it is not required for long names.
                            // As per spec, ignore trailing periods
                            xName = xLongName.Trim();

                            //If there are trailing periods
                            int nameIndex = xName.Length - 1;
                            if (xName[nameIndex] == '.')
                            {
                                //Search backwards till we find the first non-period character
                                for (; nameIndex > 0; nameIndex--)
                                {
                                    if (xName[nameIndex] != '.')
                                    {
                                        break;
                                    }
                                }
                                //Substring to remove the periods
                                xName = xName.Substring(0, nameIndex + 1);
                            }
                            xLongName = "";
                        }
                        else
                        {
                            string xEntry = xData.GetAsciiString(i, 11);
                            xName = xEntry.Substring(0, 8).TrimEnd();
                            string xExt = xEntry.Substring(8, 3).TrimEnd();
                            if (xExt.Length > 0)
                            {
                                xName = xName + "." + xExt;
                            }
                        }
                    }
                }
                UInt32 xFirstCluster = (UInt32)(xData.ToUInt16(i + 20) << 16 | xData.ToUInt16(i + 26));

                var xTest = xAttrib & (DirectoryEntryAttributeConsts.Directory | DirectoryEntryAttributeConsts.VolumeID);
                if (xAttrib == DirectoryEntryAttributeConsts.LongName)
                {
                    // skip adding, as it's a LongFileName entry, meaning the next normal entry is the item with the name.
                    FatHelpers.Debug("Entry was an Long FileName entry. Current LongName = '" + xLongName + "'");
                }
                else if (xTest == 0)
                {
                    UInt32 xSize = xData.ToUInt32(i + 28);
                    if (xSize == 0 && xName.Length == 0)
                    {
                        continue;
                    }
                    xResult.Add(new FatFile(this, xName, xSize, xFirstCluster, directoryPath));
                    FatHelpers.Debug("Returning file '" + xName + "', BaseDirectory = '" + directoryPath + "'");
                }
                else if (xTest == DirectoryEntryAttributeConsts.Directory)
                {
                    UInt32 xSize = xData.ToUInt32(i + 28);
                    var xFatDirectory = new FatDirectory(this, xName, xFirstCluster, directoryPath);
                    FatHelpers.Debug("Returning directory '" + xFatDirectory.Name + "', BaseDirectory = '" + directoryPath + "', FirstCluster = " + xFirstCluster);
                    xResult.Add(xFatDirectory);
                }
                else if (xTest == DirectoryEntryAttributeConsts.VolumeID)
                {
                    FatHelpers.DevDebug("Directory entry is VolumeID");
                    //
                }
                else
                {
                    FatHelpers.DevDebug("Not sure what to do!");
                }
            }

            return xResult;
        }

        public static bool IsDeviceFAT(Partition aDevice)
        {
            byte[] xBPB = aDevice.NewBlockArray(1);
            aDevice.ReadBlock(0UL, 1U, xBPB);
            UInt16 xSig = xBPB.ToUInt16(510);
            if (xSig != 0xAA55)
            {
                return false;
            }
            return true;
        }

        public override List<Base> GetDirectoryListing(Directory baseDirectory)
        {
            if (baseDirectory == null)
            {
                // get root folder
                return GetRoot();
            }
            else
            {
                return GetDirectoryContents((FatDirectory)baseDirectory);
            }
        }

        public override Directory GetRootDirectory(string name)
        {
            return new FatDirectory(this, name, RootCluster, null);
        }

        public override Stream GetFileStream(File fileInfo)
        {
            return new FatStream((FatFile)fileInfo);
        }

        public void SetFileLength(FatFile file, long value)
        {
            FatHelpers.Debug("File.Name:");
            FatHelpers.Debug(file.Name);
            FatHelpers.Debug("File.BaseDirectory:");
            FatHelpers.Debug(file.BaseDirectory);
            Debugger.DoSend("FatFileSystem.SetFileLength not implemented!");



            throw new NotImplementedException();
        }
    }
}
