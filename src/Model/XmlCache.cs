using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Schema;
using System.Xml.XPath;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Text;
using System.Net;
using System.Net.Cache;
using System.Timers;

namespace XmlNotepad
{   
    /// <summary>
    /// XmlCache wraps an XmlDocument and provides the stuff necessary for an "editor" in terms
    /// of watching for changes on disk, notification when the file has been reloaded, and keeping
    /// track of the current file name and dirty state.
    /// </summary>
    public class XmlCache : IDisposable
    {
        string filename;
        bool dirty;
        DomLoader loader;
        XmlDocument doc;
        FileSystemWatcher watcher;
        int retries;
        //string namespaceUri = string.Empty;
        SchemaCache schemaCache;
        Dictionary<XmlNode, XmlSchemaInfo> typeInfo;
        int batch;
        DateTime lastModified;
        Checker checker;
        IServiceProvider site;
        DelayedActions actions;
        Settings settings;

        public event EventHandler FileChanged;
        public event EventHandler<ModelChangedEventArgs> ModelChanged;

        public XmlCache(IServiceProvider site, DelayedActions handler)
        {
            this.loader = new DomLoader(site);
            this.schemaCache = new SchemaCache(site);
            this.site = site;
            this.Document = new XmlDocument();
            this.actions = handler;
            this.settings = (Settings)this.site.GetService(typeof(Settings));
        }

        ~XmlCache() {
            Dispose(false);
        }

        public Uri Location => new Uri(this.filename); 

        public string FileName => this.filename; 

        public bool IsFile {
            get {
                if (!string.IsNullOrEmpty(this.filename)) {
                    return this.Location.IsFile;
                }
                return false;
            }
        }

        /// <summary>
        /// File path to (optionally user-specified) xslt file.
        /// </summary>
        public string XsltFileName { get; set; }

        /// <summary>
        /// File path to (optionally user-specified) to use for xslt output.
        /// </summary>
        public string XsltDefaultOutput { get; set; }

        public bool Dirty => this.dirty; 

        public Settings Settings => this.settings;

        public XmlResolver SchemaResolver => this.schemaCache.Resolver;

        public XPathNavigator Navigator
        {
            get
            {
                XPathDocument xdoc = new XPathDocument(this.filename);
                XPathNavigator nav = xdoc.CreateNavigator();
                return nav;
            }
        }

        public void ValidateModel(ErrorHandler handler) {
            this.checker = new Checker(handler);
            checker.Validate(this);
        }
      

        public XmlDocument Document
        {
            get { return this.doc; }
            set
            {
                if (this.doc != null)
                {
                    this.doc.NodeChanged -= new XmlNodeChangedEventHandler(OnDocumentChanged);
                    this.doc.NodeInserted -= new XmlNodeChangedEventHandler(OnDocumentChanged);
                    this.doc.NodeRemoved -= new XmlNodeChangedEventHandler(OnDocumentChanged);
                }
                this.doc = value;
                if (this.doc != null)
                {
                    this.doc.NodeChanged += new XmlNodeChangedEventHandler(OnDocumentChanged);
                    this.doc.NodeInserted += new XmlNodeChangedEventHandler(OnDocumentChanged);
                    this.doc.NodeRemoved += new XmlNodeChangedEventHandler(OnDocumentChanged);
                }
            }
        }

        public Dictionary<XmlNode, XmlSchemaInfo> TypeInfoMap {
            get { return this.typeInfo; }
            set { this.typeInfo = value; }
        }

        public XmlSchemaInfo GetTypeInfo(XmlNode node) {
            if (this.typeInfo == null) return null;
            if (this.typeInfo.ContainsKey(node)) {
                return this.typeInfo[node];
            }
            return null;
        }

        public XmlSchemaElement GetElementType(XmlQualifiedName xmlQualifiedName)
        {
            if (this.schemaCache != null)
            {
                return this.schemaCache.GetElementType(xmlQualifiedName);
            }
            return null;
        }

        public XmlSchemaAttribute GetAttributeType(XmlQualifiedName xmlQualifiedName)
        {
            if (this.schemaCache != null)
            {
                return this.schemaCache.GetAttributeType(xmlQualifiedName);
            }
            return null;
        }

        /// <summary>
        /// Provides schemas used for validation.
        /// </summary>
        public SchemaCache SchemaCache
        {
            get { return this.schemaCache; }
            set { this.schemaCache = value; }
        }
        
        /// <summary>
        /// Loads an instance of xml.
        /// Load updated to handle validation when instance doc refers to schema.
        /// </summary>
        /// <param name="file">Xml instance document</param>
        /// <returns></returns>
        public void Load(string file)
        {
            Uri uri = new Uri(file, UriKind.RelativeOrAbsolute);
            if (!uri.IsAbsoluteUri) {
                Uri resolved = new Uri(new Uri(Directory.GetCurrentDirectory() + "\\"), uri);
                file = resolved.LocalPath;
                uri = resolved;
            }

            XmlReaderSettings settings = GetReaderSettings();
            settings.ValidationEventHandler += new ValidationEventHandler(OnValidationEvent);
            using (XmlReader reader = XmlReader.Create(file, settings))
            {
                this.Load(reader, file);
            }
        }

        public void Load(XmlReader reader, string fileName)
        {
            this.Clear();
            loader = new DomLoader(this.site);
            StopFileWatch();

            Uri uri = new Uri(fileName, UriKind.RelativeOrAbsolute);
            if (!uri.IsAbsoluteUri)
            {
                Uri resolved = new Uri(new Uri(Directory.GetCurrentDirectory() + "\\"), uri);
                fileName = resolved.LocalPath;
                uri = resolved;
            }

            this.filename = fileName;
            this.lastModified = this.LastModTime;
            this.dirty = false;
            StartFileWatch();

            this.Document = loader.Load(reader);
            this.XsltFileName = this.loader.XsltFileName;
            this.XsltDefaultOutput = this.loader.XsltDefaultOutput;

            // calling this event will cause the XmlTreeView to populate
            FireModelChanged(ModelChangeType.Reloaded, this.doc);
        }

        public XmlReaderSettings GetReaderSettings() {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = this.settings.GetBoolean("IgnoreDTD") ? DtdProcessing.Ignore : DtdProcessing.Parse;
            settings.CheckCharacters = false;
            settings.XmlResolver = Settings.Instance.Resolver;
            return settings;
        }

        public void ExpandIncludes() {
            if (this.Document != null) {
                this.dirty = true;
                XmlReaderSettings s = new XmlReaderSettings();
                s.DtdProcessing = this.settings.GetBoolean("IgnoreDTD") ? DtdProcessing.Ignore : DtdProcessing.Parse;
                s.XmlResolver = Settings.Instance.Resolver;
                using (XmlReader r = XmlIncludeReader.CreateIncludeReader(this.Document, s, this.FileName)) {
                    this.Document = loader.Load(r);
                }

                // calling this event will cause the XmlTreeView to populate
                FireModelChanged(ModelChangeType.Reloaded, this.doc);
            }
        }

        public void BeginUpdate() {
            if (batch == 0)
                FireModelChanged(ModelChangeType.BeginBatchUpdate, this.doc);
            batch++;
        }

        public void EndUpdate() {
            batch--;
            if (batch == 0)
                FireModelChanged(ModelChangeType.EndBatchUpdate, this.doc);
        }

        public LineInfo GetLineInfo(XmlNode node) {
            return loader.GetLineInfo(node);
        }

        void OnValidationEvent(object sender, ValidationEventArgs e)
        {
            // todo: log errors in error list window.
        }                

        public void Reload()
        {
            string filename = this.filename;
            Clear();
            Load(filename);
        }

        public void Clear()
        {
            this.Document = new XmlDocument();
            StopFileWatch();
            this.filename = null;
            FireModelChanged(ModelChangeType.Reloaded, this.doc);
        }

        public void Save()
        {
            Save(this.filename);
        }

        public Encoding GetEncoding() {
            XmlDeclaration xmldecl = doc.FirstChild as XmlDeclaration;
            Encoding result = null;
            if (xmldecl != null)
            {
                string name = "";
                try
                {
                    name = xmldecl.Encoding;
                    if (!string.IsNullOrEmpty(name))
                    {
                        result = Encoding.GetEncoding(name);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format("Error getting encoding '{0}': {1}", name, ex.Message));
                }
            }
            if (result == null)
            {
                // default is UTF-8.
                result = Encoding.UTF8;
            }
            return result;
        }
        
        public void AddXmlDeclarationWithEncoding()
        {
            XmlDeclaration xmldecl = doc.FirstChild as XmlDeclaration;
            if (xmldecl == null)
            {
                doc.InsertBefore(doc.CreateXmlDeclaration("1.0", "utf-8", null), doc.FirstChild);
            }
            else
            {
                string e = xmldecl.Encoding;
                if (string.IsNullOrEmpty(e))
                {
                    xmldecl.Encoding = "utf-8";
                }
            }
        }

        public void Save(string name)
        {
            SaveCopy(name);
            this.dirty = false;
            this.filename = name;
            this.lastModified = this.LastModTime;
            FireModelChanged(ModelChangeType.Saved, this.doc);
        }

        public void SaveCopy(string filename)
        {
            try
            {
                StopFileWatch();
                XmlWriterSettings s = new XmlWriterSettings();
                Utilities.InitializeWriterSettings(s, this.site);

                var encoding = GetEncoding();
                s.Encoding = encoding;
                bool noBom = false;
                if (this.site != null)
                {
                    Settings settings = (Settings)this.site.GetService(typeof(Settings));
                    if (settings != null)
                    {
                        noBom = (bool)settings["NoByteOrderMark"];
                        if (noBom)
                        {
                            // then we must have an XML declaration with an encoding attribute.
                            AddXmlDeclarationWithEncoding();
                        }
                    }
                }
                if (noBom)
                {
                    MemoryStream ms = new MemoryStream();
                    using (XmlWriter w = XmlWriter.Create(ms, s))
                    {
                        doc.Save(w);
                    }
                    ms.Seek(0, SeekOrigin.Begin);

                    Utilities.WriteFileWithoutBOM(ms, filename);

                }
                else
                {
                    using (XmlWriter w = XmlWriter.Create(filename, s))
                    {
                        doc.Save(w);
                    }
                }
            }
            finally
            {
                StartFileWatch();
            }
        }

        public bool IsReadOnly(string filename) {
            return File.Exists(filename) &&
                (File.GetAttributes(filename) & FileAttributes.ReadOnly) != 0;
        }

        public void MakeReadWrite(string filename) {
            if (!File.Exists(filename))
                return;

            StopFileWatch();
            try {
                FileAttributes attrsMinusReadOnly = File.GetAttributes(this.filename) & ~FileAttributes.ReadOnly;
                File.SetAttributes(filename, attrsMinusReadOnly);
            } finally {
                StartFileWatch();
            }           
        }

        void StopFileWatch()
        {
            if (this.watcher != null)
            {
                this.watcher.Dispose();
                this.watcher = null;
            }
        }
        private void StartFileWatch()
        {
            if (this.filename != null && Location.IsFile && File.Exists(this.filename))
            {
                string dir = Path.GetDirectoryName(this.filename) + "\\";
                this.watcher = new FileSystemWatcher(dir, "*.*");
                this.watcher.Changed += new FileSystemEventHandler(watcher_Changed);
                this.watcher.Renamed += new RenamedEventHandler(watcher_Renamed);
                this.watcher.EnableRaisingEvents = true;
            }
            else
            {
                StopFileWatch();
            }
        }

        void StartReload()
        {
            // Apart from retrying, the DelayedActions has the nice side effect of also 
            // collapsing multiple file system events into one action callback.
            retries = 3;
            actions.StartDelayedAction("reload", CheckReload, TimeSpan.FromSeconds(1));
        }

        DateTime LastModTime {
            get {
                if (Location.IsFile) return File.GetLastWriteTime(this.filename);
                return DateTime.Now;
            }
        }

        void CheckReload()
        {
            try
            {
                // Only do the reload if the file on disk really is different from
                // what we last loaded.
                if (this.lastModified < LastModTime) {

                    // Test if we can open the file (it might still be locked).
                    using (FileStream fs = new FileStream(this.filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Close();
                    }

                    FireFileChanged();
                }
            }
            finally
            {
                retries--;
                if (retries > 0)
                {
                    actions.StartDelayedAction("reload", Reload, TimeSpan.FromSeconds(1));
                }
            }
        }

        private void watcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed && 
                IsSamePath(this.filename, e.FullPath))
            {
                StartReload();
            }
        }

        private void watcher_Renamed(object sender, RenamedEventArgs e)
        {
            // Some editors rename the file to *.bak then save the new version and
            // in that case we do not want XmlNotepad to switch to the .bak file.
            if (IsSamePath(this.filename, e.OldFullPath))
            {
                // switch to UI thread
                actions.StartDelayedAction("renamed", OnRenamed, TimeSpan.FromMilliseconds(1));
            }
        }

        private void OnRenamed()
        {
            this.dirty = true;
            FireModelChanged(ModelChangeType.Renamed, this.doc);
        }

        static bool IsSamePath(string a, string b)
        {
            return string.Compare(a, b, true) == 0;
        }

        void FireFileChanged()
        {
            if (this.FileChanged != null)
            {
                FileChanged(this, EventArgs.Empty);
            }
        }

        void FireModelChanged(ModelChangeType t, XmlNode node)
        {
            if (this.ModelChanged != null)
                this.ModelChanged(this, new ModelChangedEventArgs(t, node));
        }

        void OnPIChange(XmlNodeChangedEventArgs e) {
            XmlProcessingInstruction pi = (XmlProcessingInstruction)e.Node;
            if (pi.Name == "xml-stylesheet") {
                if (e.Action == XmlNodeChangedAction.Remove) {
                    // see if there's another!
                    pi = this.doc.SelectSingleNode("processing-instruction('xml-stylesheet')") as XmlProcessingInstruction;
                }
                if (pi != null) {
                    this.XsltFileName = DomLoader.ParseXsltArgs(pi.Data);
                }
                else
                {
                    this.XsltFileName = null;
                }
            }
            else if (pi.Name == "xsl-output")
            {
                if (e.Action == XmlNodeChangedAction.Remove)
                {
                    // see if there's another!
                    pi = this.doc.SelectSingleNode("processing-instruction('xsl-output')") as XmlProcessingInstruction;
                }
                if (pi != null)
                {
                    this.XsltDefaultOutput = DomLoader.ParseXsltOutputArgs(pi.Data);
                }
                else
                {
                    this.XsltDefaultOutput = null;
                }
            }
        }

        private void OnDocumentChanged(object sender, XmlNodeChangedEventArgs e)
        {
            // initialize t
            ModelChangeType t = ModelChangeType.NodeChanged;
            if (e.Node is XmlProcessingInstruction) {
                OnPIChange(e);
            }

            if (XmlHelpers.IsXmlnsNode(e.NewParent) || XmlHelpers.IsXmlnsNode(e.Node)) {

                // we flag a namespace change whenever an xmlns attribute changes.
                t = ModelChangeType.NamespaceChanged;
                XmlNode node = e.Node;
                if (e.Action == XmlNodeChangedAction.Remove) {
                    node = e.OldParent; // since node.OwnerElement link has been severed!
                }
                this.dirty = true;
                FireModelChanged(t, node);
            } else {
                switch (e.Action) {
                    case XmlNodeChangedAction.Change:
                        t = ModelChangeType.NodeChanged;
                        break;
                    case XmlNodeChangedAction.Insert:
                        t = ModelChangeType.NodeInserted;
                        break;
                    case XmlNodeChangedAction.Remove:
                        t = ModelChangeType.NodeRemoved;
                        break;
                }
                this.dirty = true;
                FireModelChanged(t, e.Node);
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            this.actions.CancelDelayedAction("reload");
            StopFileWatch();
        }

    }

    public enum ModelChangeType
    {
        Reloaded,
        Saved,
        NodeChanged,
        NodeInserted,
        NodeRemoved,
        NamespaceChanged,
        BeginBatchUpdate,
        EndBatchUpdate,
        Renamed,
    }

    public class ModelChangedEventArgs : EventArgs
    {
        ModelChangeType type;
        XmlNode node;

        public ModelChangedEventArgs(ModelChangeType t, XmlNode node)
        {
            this.type = t;
            this.node = node;
        }

        public XmlNode Node {
            get { return node; }
            set { node = value; }
        }

        public ModelChangeType ModelChangeType
        {
            get { return this.type; }
            set { this.type = value; }
        }

    }

    public enum IndentChar {
        Space,
        Tab
    }
}