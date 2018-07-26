﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace SimplePatchToolCore
{
	public static class PatchUtils
	{
		#region Constants
		private const double BYTES_TO_KB_MULTIPLIER = 1.0 / 1024;
		private const double BYTES_TO_MB_MULTIPLIER = 1.0 / ( 1024 * 1024 );
		#endregion

		#region Extension Functions
		public static string ElapsedSeconds( this Stopwatch timer )
		{
			return ( timer.ElapsedMilliseconds / 1000.0 ).ToString( "F3" );
		}

		public static string ToKilobytes( this long bytes )
		{
			return ( bytes * BYTES_TO_KB_MULTIPLIER ).ToString( "F2" );
		}

		public static string ToMegabytes( this long bytes )
		{
			return ( bytes * BYTES_TO_MB_MULTIPLIER ).ToString( "F2" );
		}

		public static string Md5Hash( this FileInfo fileInfo )
		{
			using( var md5 = MD5.Create() )
			{
				using( var stream = File.OpenRead( fileInfo.FullName ) )
				{
					return BitConverter.ToString( md5.ComputeHash( stream ) ).Replace( "-", "" ).ToLowerInvariant();
				}
			}
		}

		public static bool MatchesSignature( this FileInfo fileInfo, long fileSize, string md5 )
		{
			if( fileInfo.Length == fileSize )
				return fileSize > PatchParameters.FileHashCheckSizeLimit || fileInfo.Md5Hash() == md5;

			return false;
		}

		public static bool MatchesSignature( this FileInfo fileInfo, FileInfo other )
		{
			long fileSize = other.Length;
			if( fileInfo.Length == fileSize )
				return fileSize > PatchParameters.FileHashCheckSizeLimit || fileInfo.Md5Hash() == other.Md5Hash();

			return false;
		}

		public static bool PathMatchesPattern( this IEnumerable<Regex> patterns, string path )
		{
			foreach( Regex pattern in patterns )
			{
				if( pattern.IsMatch( path ) )
					return true;
			}

			return false;
		}

		public static string PatchVersionBrief( this IncrementalPatch incrementalPatch )
		{
			return incrementalPatch.FromVersion + "_" + incrementalPatch.ToVersion;
		}

		public static string PatchVersion( this IncrementalPatch incrementalPatch )
		{
			return incrementalPatch.FromVersion.ToString().Replace( '.', '_' ) + "__" + incrementalPatch.ToVersion.ToString().Replace( '.', '_' );
		}

		public static string PatchVersion( this PatchInfo patchInfo )
		{
			return patchInfo.FromVersion.ToString().Replace( '.', '_' ) + "__" + patchInfo.ToVersion.ToString().Replace( '.', '_' );
		}
		#endregion

		#region XML Functions
		public static void SerializeVersionInfoToXML( VersionInfo version, string xmlPath )
		{
			var serializer = new XmlSerializer( typeof( VersionInfo ) );
			using( var stream = new FileStream( xmlPath, FileMode.Create ) )
			{
				serializer.Serialize( stream, version );
			}
		}

		public static VersionInfo DeserializeXMLToVersionInfo( string xmlContent )
		{
			var serializer = new XmlSerializer( typeof( VersionInfo ) );
			using( var stream = new StringReader( xmlContent ) )
			{
				VersionInfo result = serializer.Deserialize( stream ) as VersionInfo;
				if( result != null )
				{
					result.Patches.RemoveAll( ( patch ) => !patch.FromVersion.IsValid || !patch.ToVersion.IsValid || patch.ToVersion <= patch.FromVersion );
					result.Patches.Sort( IncrementalPatchComparison );

					// Always use Path.DirectorySeparatorChar
					for( int i = 0; i < result.Files.Count; i++ )
					{
						VersionItem item = result.Files[i];
						item.Path = item.Path.Replace( Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar );
					}

					result.IgnoredPaths.Add( "*" + PatchParameters.VERSION_HOLDER_FILENAME_POSTFIX );
					result.IgnoredPathsRegex = new Regex[result.IgnoredPaths.Count];
					for( int i = 0; i < result.IgnoredPaths.Count; i++ )
						result.IgnoredPathsRegex[i] = WildcardToRegex( result.IgnoredPaths[i].Replace( Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar ) );
				}

				return result;
			}
		}

		public static void SerializePatchInfoToXML( PatchInfo patch, string xmlPath )
		{
			var serializer = new XmlSerializer( typeof( PatchInfo ) );
			using( var stream = new FileStream( xmlPath, FileMode.Create ) )
			{
				serializer.Serialize( stream, patch );
			}
		}

		public static PatchInfo DeserializeXMLToPatchInfo( string xmlContent )
		{
			var serializer = new XmlSerializer( typeof( PatchInfo ) );
			using( var stream = new StringReader( xmlContent ) )
			{
				PatchInfo result = serializer.Deserialize( stream ) as PatchInfo;
				if( result != null )
				{
					// Always use Path.DirectorySeparatorChar
					for( int i = 0; i < result.RenamedFiles.Count; i++ )
					{
						PatchRenamedItem renamedItem = result.RenamedFiles[i];
						renamedItem.BeforePath = renamedItem.BeforePath.Replace( Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar );
						renamedItem.AfterPath = renamedItem.AfterPath.Replace( Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar );
					}

					for( int i = 0; i < result.Files.Count; i++ )
					{
						PatchItem patchItem = result.Files[i];
						patchItem.Path = patchItem.Path.Replace( Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar );
					}
				}

				return result;
			}
		}

		private static int IncrementalPatchComparison( IncrementalPatch patch1, IncrementalPatch patch2 )
		{
			int comparison = patch1.FromVersion.CompareTo( patch2.FromVersion );
			if( comparison != 0 )
				return comparison;

			// If FromVersion's are the same, the patch with higher ToVersion should be put in front of the other
			return patch2.ToVersion.CompareTo( patch1.ToVersion );
		}
		#endregion

		#region IO Functions
		public static bool CheckWriteAccessToFolder( string path )
		{
			path = GetPathWithTrailingSeparatorChar( path );
			string testFilePath = path + "_test__.sptest";

			try
			{
				if( !Directory.Exists( path ) )
					Directory.CreateDirectory( path );

				File.Create( testFilePath ).Close();

				// Check if file is created inside VirtualStore directory (UAC-issue)
				string root = Path.GetPathRoot( testFilePath );
				string virtualPathRelative = Path.Combine( "VirtualStore", testFilePath.Substring( root.Length ) );
				string virtualPathAbsolute = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), virtualPathRelative );
				try
				{
					if( File.Exists( virtualPathAbsolute ) )
						return false;
				}
				catch { }

				virtualPathAbsolute = Path.Combine( root, virtualPathRelative );
				try
				{
					if( File.Exists( virtualPathAbsolute ) )
						return false;
				}
				catch { }
			}
			catch( UnauthorizedAccessException )
			{
				return false;
			}
			catch( SecurityException )
			{
				return false;
			}
			finally
			{
				try
				{
					if( File.Exists( testFilePath ) )
						File.Delete( testFilePath );
				}
				catch { }
			}

			return true;
		}

		public static void MoveFile( string fromAbsolutePath, string toAbsolutePath )
		{
			if( File.Exists( toAbsolutePath ) )
			{
				File.Copy( fromAbsolutePath, toAbsolutePath, true );
				File.Delete( fromAbsolutePath );
			}
			else
			{
				Directory.CreateDirectory( Path.GetDirectoryName( toAbsolutePath ) );
				File.Move( fromAbsolutePath, toAbsolutePath );
			}
		}

		public static void MoveDirectory( string fromAbsolutePath, string toAbsolutePath )
		{
			if( Directory.Exists( toAbsolutePath ) )
				MergeDirectories( fromAbsolutePath, toAbsolutePath );
			else
			{
				Directory.CreateDirectory( Directory.GetParent( toAbsolutePath ).FullName );
				Directory.Move( fromAbsolutePath, toAbsolutePath );
			}
		}

		public static void MergeDirectories( string fromAbsolutePath, string toAbsolutePath )
		{
			toAbsolutePath = GetPathWithTrailingSeparatorChar( toAbsolutePath );

			MergeDirectories( new DirectoryInfo( fromAbsolutePath ), new DirectoryInfo( toAbsolutePath ), toAbsolutePath );
			Directory.Delete( fromAbsolutePath, true );
		}

		private static void MergeDirectories( DirectoryInfo from, DirectoryInfo to, string targetAbsolutePath )
		{
			FileInfo[] files = from.GetFiles();
			for( int i = 0; i < files.Length; i++ )
			{
				FileInfo fileInfo = files[i];
				fileInfo.CopyTo( targetAbsolutePath + fileInfo.Name, true );
			}

			DirectoryInfo[] subDirectories = from.GetDirectories();
			for( int i = 0; i < subDirectories.Length; i++ )
			{
				DirectoryInfo directoryInfo = subDirectories[i];
				string directoryAbsolutePath = targetAbsolutePath + directoryInfo.Name + Path.DirectorySeparatorChar;
				if( Directory.Exists( directoryAbsolutePath ) )
					MergeDirectories( directoryInfo, new DirectoryInfo( directoryAbsolutePath ), directoryAbsolutePath );
				else
					directoryInfo.MoveTo( directoryAbsolutePath );
			}
		}

		public static VersionInfo GetVersionInfoFromPath( string path )
		{
			try
			{
				string xmlContent = File.ReadAllText( path );
				return DeserializeXMLToVersionInfo( xmlContent );
			}
			catch
			{
				return null;
			}
		}

		public static PatchInfo GetPatchInfoFromPath( string path )
		{
			try
			{
				string xmlContent = File.ReadAllText( path );
				return DeserializeXMLToPatchInfo( xmlContent );
			}
			catch
			{
				return null;
			}
		}

		public static VersionCode GetVersion( string rootPath, string projectName )
		{
			rootPath = GetPathWithTrailingSeparatorChar( rootPath ) + projectName + PatchParameters.VERSION_HOLDER_FILENAME_POSTFIX;
			return File.Exists( rootPath ) ? File.ReadAllText( rootPath ) : null;
		}

		public static void SetVersion( string rootPath, string projectName, VersionCode version )
		{
			Directory.CreateDirectory( rootPath );
			File.WriteAllText( GetPathWithTrailingSeparatorChar( rootPath ) + projectName + PatchParameters.VERSION_HOLDER_FILENAME_POSTFIX, version );
		}
		#endregion

		#region Other Functions
		public static string GetPathWithTrailingSeparatorChar( string path )
		{
			char trailingChar = path[path.Length - 1];
			if( trailingChar != Path.DirectorySeparatorChar && trailingChar != Path.AltDirectorySeparatorChar )
				path += Path.DirectorySeparatorChar;

			return path;
		}

		public static string GetCurrentExecutablePath()
		{
			using( Process process = Process.GetCurrentProcess() )
			{
				using( ProcessModule mainModule = process.MainModule )
				{
					return mainModule.FileName;
				}
			}
		}

		public static int GetNumberOfRunningProcesses( string executablePath )
		{
			if( !File.Exists( executablePath ) )
				return 0;

			int result = 0;
			try
			{
				string executableFullPath = Path.GetFullPath( executablePath );
				Process[] processes = Process.GetProcessesByName( Path.GetFileNameWithoutExtension( executablePath ) );
				for( int i = 0; i < processes.Length; i++ )
				{
					try
					{
						using( Process process = processes[i] )
						{
							using( ProcessModule mainModule = process.MainModule )
							{
								if( executableFullPath.Equals( Path.GetFullPath( mainModule.FileName ), StringComparison.OrdinalIgnoreCase ) )
									result++;
							}
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
				result = 0;
			}

			return result;
		}

		public static bool IsProjectNameValid( string projectName )
		{
			if( string.IsNullOrEmpty( projectName ) )
				return false;

			for( int i = 0; i < projectName.Length; i++ )
			{
				char ch = projectName[i];
				if( ( ch < '0' || ch > '9' ) && ( ch < 'a' || ch > 'z' ) && ( ch < 'A' || ch > 'Z' ) )
					return false;
			}

			return true;
		}

		public static bool TryParseIntSimple( string s, out int result )
		{
			result = 0;
			if( string.IsNullOrEmpty( s ) )
				return false;

			bool isNegative = s[0] == '-';
			for( int i = isNegative ? 1 : 0; i < s.Length; i++ )
			{
				char ch = s[i];
				if( ch < '0' || ch > '9' )
					return false;

				result = result * 10 + ( ch - '0' );
			}

			if( isNegative )
				result = -result;

			return true;
		}

		public static Regex WildcardToRegex( string wildcardStr )
		{
			return new Regex( "^" + Regex.Escape( wildcardStr ).Replace( "\\?", "." ).Replace( "\\*", ".*" ) + "$", RegexOptions.None );
		}

		// Handles 3 kinds of links (they can be preceeded by https://):
		// - drive.google.com/open?id=FILEID
		// - drive.google.com/file/d/FILEID/view?usp=sharing
		// - drive.google.com/uc?id=FILEID&export=download
		public static string GetGoogleDriveDownloadLinkFromUrl( string url )
		{
			int index = url.IndexOf( "id=" );
			int closingIndex;
			if( index > 0 )
			{
				index += 3;
				closingIndex = url.IndexOf( '&', index );
				if( closingIndex < 0 )
					closingIndex = url.Length;
			}
			else
			{
				index = url.IndexOf( "file/d/" );
				if( index < 0 ) // url is not in any of the supported forms
					return string.Empty;

				index += 7;

				closingIndex = url.IndexOf( '/', index );
				if( closingIndex < 0 )
				{
					closingIndex = url.IndexOf( '?', index );
					if( closingIndex < 0 )
						closingIndex = url.Length;
				}
			}

			return string.Format( "https://drive.google.com/uc?id={0}&export=download", url.Substring( index, closingIndex - index ) );
		}
		#endregion
	}
}