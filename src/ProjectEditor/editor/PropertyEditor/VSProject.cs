// ***********************************************************************
// Copyright (c) 2018 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.IO;
using System.Xml;
using System.Text.RegularExpressions;

namespace NUnit.ProjectEditor
{
	/// <summary>
	/// This class allows loading information about
	/// configurations and assemblies in a Visual
	/// Studio doc file and inspecting them.
	/// Only the most common doc types are
	/// supported and an exception is thrown if
	/// an attempt is made to load an invalid
	/// file or one of an unknown type.
	/// </summary>
	public class VSProject
	{
		#region Static and Instance Variables

		/// <summary>
		/// VS Project extentions
		/// </summary>
		private static readonly string[] validExtensions = { ".csproj", ".vbproj", ".vjsproj", ".vcproj" };
		
		/// <summary>
		/// VS Solution extension
		/// </summary>
		private static readonly string solutionExtension = ".sln";

		/// <summary>
		/// Path to the file storing this doc
		/// </summary>
		private string projectPath;

		/// <summary>
		/// Collection of configs for the doc
		/// </summary>
		private VSProjectConfigCollection configs;

		#endregion

		#region Constructor

		public VSProject( string projectPath )
		{
			this.projectPath = Path.GetFullPath( projectPath );
			configs = new VSProjectConfigCollection();		

			Load();
		}

		#endregion

		#region Properties

		/// <summary>
		/// The name of the doc.
		/// </summary>
		public string Name
		{
			get { return Path.GetFileNameWithoutExtension( projectPath ); }
		}

		/// <summary>
		/// The path to the doc
		/// </summary>
		public string ProjectPath
		{
			get { return projectPath; }
		}

		/// <summary>
		/// Our collection of configurations
		/// </summary>
		public VSProjectConfigCollection Configs
		{
			get { return configs; }
		}

		#endregion

		#region Static Methods

		public static bool IsProjectFile( string path )
		{
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;

			if ( path.ToLower().IndexOf( "http:" ) >= 0 )
				return false;
		
			string extension = Path.GetExtension( path );

			foreach( string validExtension in validExtensions )
				if ( extension == validExtension )
					return true;

			return false;
		}

		public static bool IsSolutionFile( string path )
		{
			return Path.GetExtension( path ) == solutionExtension;
		}

		#endregion

		#region Instance Methods

		private void Load()
		{
			if ( !IsProjectFile( projectPath ) ) 
				ThrowInvalidFileType( projectPath );

			string projectDirectory = Path.GetFullPath( Path.GetDirectoryName( projectPath ) );
			StreamReader rdr = new StreamReader( projectPath, System.Text.Encoding.UTF8 );
			string[] extensions = {"", ".exe", ".dll", ".lib", "" };
			
			try
			{
				XmlDocument doc = new XmlDocument();
				doc.Load( rdr );

				string extension = Path.GetExtension( projectPath );
				string assemblyName = null;

				switch ( extension )
				{
					case ".vcproj":
						XmlNode topNode = doc.SelectSingleNode( "/VisualStudioProject" );

						// TODO: This is all very hacked up... replace it.
						foreach ( XmlNode configNode in doc.SelectNodes( "/VisualStudioProject/Configurations/Configuration" ) )
						{
							string name = RequiredAttributeValue( configNode, "Name" );
							int config_type = System.Convert.ToInt32(RequiredAttributeValue(configNode, "ConfigurationType" ) );
							string dirName = name;
							int bar = dirName.IndexOf( '|' );
							if ( bar >= 0 )
								dirName = dirName.Substring( 0, bar );
							string outputPath = RequiredAttributeValue( configNode, "OutputDirectory" );
							outputPath = outputPath.Replace( "$(SolutionDir)", Path.GetFullPath( Path.GetDirectoryName( projectPath ) ) + Path.DirectorySeparatorChar );
							outputPath = outputPath.Replace( "$(ConfigToCreate)", dirName );

							string outputDirectory = Path.Combine( projectDirectory, outputPath );
							XmlNode toolNode = configNode.SelectSingleNode( "Tool[@Name='VCLinkerTool']" );
							if ( toolNode != null )
							{
								assemblyName = SafeAttributeValue( toolNode, "OutputFile" );
								if ( assemblyName != null )
									assemblyName = Path.GetFileName( assemblyName );
								else
									assemblyName = Path.GetFileNameWithoutExtension(projectPath) + extensions[config_type];
							}
							else
							{
								toolNode = configNode.SelectSingleNode( "Tool[@Name='VCNMakeTool']" );
								if ( toolNode != null )
									assemblyName = Path.GetFileName( RequiredAttributeValue( toolNode, "Output" ) );
							}

							assemblyName = assemblyName.Replace( "$(OutDir)", outputPath );
							assemblyName = assemblyName.Replace( "$(ProjectName)", this.Name );

							VSProjectConfig config = new VSProjectConfig ( name );
							if ( assemblyName != null )
								config.Assemblies.Add( Path.Combine( outputDirectory, assemblyName ) );
							
							this.configs.Add( config );
						}
					
						break;

					case ".csproj":
					case ".vbproj":
					case ".vjsproj":
						LoadProject( projectDirectory, doc );
						break;

					default:
						break;
				}
			}
			catch( FileNotFoundException )
			{
				throw;
			}
			catch( Exception e )
			{
				ThrowInvalidFormat( projectPath, e );
			}
			finally
			{
				rdr.Close();
			}
		}

		private bool LoadProject(string projectDirectory, XmlDocument doc)
		{
			bool loaded = LoadVS2003Project(projectDirectory, doc);
			if (loaded) return true;

			loaded = LoadMSBuildProject(projectDirectory, doc);
			if (loaded) return true;

			return false;
		}

		private bool LoadVS2003Project(string projectDirectory, XmlDocument doc)
		{
			XmlNode settingsNode = doc.SelectSingleNode("/VisualStudioProject/*/Build/Settings");
			if (settingsNode == null)
				return false;

			string assemblyName = RequiredAttributeValue( settingsNode, "AssemblyName" );
			string outputType = RequiredAttributeValue( settingsNode, "OutputType" );

			if (outputType == "Exe" || outputType == "WinExe")
				assemblyName = assemblyName + ".exe";
			else
				assemblyName = assemblyName + ".dll";

			XmlNodeList nodes = settingsNode.SelectNodes("Config");
			if (nodes != null)
				foreach (XmlNode configNode in nodes)
				{
					string name = RequiredAttributeValue( configNode, "Name" );
					string outputPath = RequiredAttributeValue( configNode, "OutputPath" );
					string outputDirectory = Path.Combine(projectDirectory, outputPath);
					string assemblyPath = Path.Combine(outputDirectory, assemblyName);

					VSProjectConfig config = new VSProjectConfig(name);
					config.Assemblies.Add(assemblyPath);

					configs.Add(config);
				}

			return true;
		}

		private bool LoadMSBuildProject(string projectDirectory, XmlDocument doc)
		{
			XmlNamespaceManager namespaceManager = new XmlNamespaceManager(doc.NameTable);
			namespaceManager.AddNamespace("msbuild", "http://schemas.microsoft.com/developer/msbuild/2003");

			XmlNodeList nodes = doc.SelectNodes("/msbuild:Project/msbuild:PropertyGroup", namespaceManager);
			if (nodes == null) return false;

			XmlElement assemblyNameElement = (XmlElement)doc.SelectSingleNode("/msbuild:Project/msbuild:PropertyGroup/msbuild:AssemblyName", namespaceManager);
			string assemblyName = assemblyNameElement.InnerText;

			XmlElement outputTypeElement = (XmlElement)doc.SelectSingleNode("/msbuild:Project/msbuild:PropertyGroup/msbuild:OutputType", namespaceManager);
			string outputType = outputTypeElement.InnerText;

			if (outputType == "Exe" || outputType == "WinExe")
				assemblyName = assemblyName + ".exe";
			else
				assemblyName = assemblyName + ".dll";

			foreach (XmlElement configNode in nodes)
			{
                if (configNode.Name != "PropertyGroup")
                    continue;

				XmlAttribute conditionAttribute = configNode.Attributes["Condition"];
				if (conditionAttribute == null) continue;

				string condition = conditionAttribute.Value;
				int start = condition.IndexOf( "==" );
				if ( start < 0 ) continue;

				string configurationName = condition.Substring( start + 2 ).Trim(new char[] {' ', '\'' } );
				if ( configurationName.EndsWith( "|AnyCPU" ) )
					configurationName = configurationName.Substring( 0, configurationName.Length - 7 );

				XmlElement outputPathElement = (XmlElement)configNode.SelectSingleNode("msbuild:OutputPath", namespaceManager);
				string outputPath = outputPathElement.InnerText;

				string outputDirectory = Path.Combine(projectDirectory, outputPath);
				string assemblyPath = Path.Combine(outputDirectory, assemblyName);

				VSProjectConfig config = new VSProjectConfig(configurationName);
				config.Assemblies.Add(assemblyPath);

				configs.Add(config);
			}

			return true;
		}

		private void ThrowInvalidFileType(string projectPath)
		{
			throw new ArgumentException( 
				string.Format( "Invalid model file type: {0}", 
								Path.GetFileName( projectPath ) ) );
		}

		private void ThrowInvalidFormat( string projectPath, Exception e )
		{
			throw new ArgumentException( 
				string.Format( "Invalid model file format: {0}", 
								Path.GetFileName( projectPath ) ), e );
		}

		private string SafeAttributeValue( XmlNode node, string attrName )
		{
			XmlNode attrNode = node.Attributes[attrName];
			return attrNode == null ? null : attrNode.Value;
		}

		private string RequiredAttributeValue( XmlNode node, string name )
		{
			string result = SafeAttributeValue( node, name );
			if ( result != null )
				return result;

			throw new ApplicationException( "Missing required attribute " + name );
		}
		#endregion
	}
}
