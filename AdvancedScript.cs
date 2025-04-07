using bcnvision.Data;
using bcnvision.Tools;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using bcnvision.Communications;
using System.IO;
using System.Collections.Generic;
using System.Xml.Serialization;
using bcnvision.Vidi;
using System.Reflection;
using static bcnvision.Data.BcnAdvScriptConfigFile;
using System.Windows.Media.Media3D;
using System.Drawing;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using BCNVision.Client;
using static bcnvision.Global;
using System.Net.Mail;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Windows.Threading;

namespace bcnvision
{
    public class AdvancedScript : BcnAdvancedScript
    {

        #region Enums
        enum Defecto
        {
            DesplazamientoEtiqueta,
            AusenciaLote,
            LoteIncorrecto,
            ContenidoEtiqueta,
            Precio,
            Macula,
            Inorganico,
            Organico,
            Calidez,
            Montaje,
            AusenciaProducto
        }
        #endregion

        #region Constants
        //public string SetLote { get; set; }
        private BcnTcpServer server;
        public BcnTcpClient Client;
        string IPServerTCP = "10.145.127.10";
        //string IPServerTCP = "127.0.0.1";
        int PortClient = 56423;
        int PortServer = 56426;
        string[] Lotes11 = { "", "" };
        //int PortServer = 3000;
        private ManualResetEvent askLoteCompleted = new ManualResetEvent(false);
        private readonly object lockObject = new object();
        // Diccionario para almacenar los dos últimos lotes por cada línea de producción
        private Dictionary<string, List<string>> lotesPorLinea = new Dictionary<string, List<string>>();
        private Dictionary<string, string> lotesManuales = new Dictionary<string, string>();
        private HashSet<string> lineasEnManual = new HashSet<string>();
        #endregion

        #region Properties
        /// <summary>
        /// Vista de la clase
        /// </summary>
        public MainWindow MainView { get; set; } = new MainWindow();
        //public MainWindow MainView { get; set; }
        public BcnUdp udp { get; set; }
        private string SetLote = "";
        #endregion

        #region Fields

        /// <summary>
        /// Listado de estaciones
        /// </summary>
        private List<VisionStation> VisionStations = new List<VisionStation>();

        private AdvancedScriptConfiguration AdvancedScriptConfiguration;

        BcnVidi Vidi = new BcnVidi();
        /// <summary>
        /// Instancia de BcnFolderManager
        /// </summary>
        private BcnFolderManager folderManager = new BcnFolderManager();

#if ISDB
        private DB DB;
#endif


        // Ruta y nombre del archivo CSV para guardar los resultados
        private string csvFilePath = "G:\\bcnvision\\Ubago\\Prototipo Bandejas\\Default\\Defectos\\Defectos.csv";
        private bool[] defectos;
        private bool codigo;
        private Queue<(string lote, DateTime timestamp)> loteBuffer = new Queue<(string, DateTime)>();
        private int bufferCapacity = 2; // Ajusta según la cantidad de lotes en tránsito
        private bool IsDummyRunning;
        private string[] ListRecipes;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="Udp">Dispostivo de comunicacion</param>
        public AdvancedScript()
        {
            try
            {
                ListRecipes = new string[BcnConfigFile.Configuration.VisionSystems.Count];

                logger.Fatal("ARRANCAMOS COMO CLIENTE TCP");

                //TCP Client                
                Client = new BcnTcpClient(PortClient, IPServerTCP, true);
                Client.Start();
                Client.OnMessageEvent += Client_OnMessageEvent;
                System.Threading.Thread.Sleep(500);
                //TCP Server                
                server = new BcnTcpServer(PortServer);
                server.Start();
                server.OnNewClient += server_OnNewClient;
                server.OnValueChanged += Server_OnValueChanged;

                for (int i = 0; i < 5; i++)
                {
                    Client?.Write((i + 1).ToString("00"));
                    System.Threading.Thread.Sleep(500);
                    UpdateLote((i + 1).ToString("00"), i.ToString("00"));
                }


                System.Threading.Thread.Sleep(5000);

                if (lotesPorLinea.Count > 0)
                {
                    logger.Info("Lote cargado");
                    logger.Info("Cliente TCP cerrado");
                    Client.Dispose();
                }
                else
                {
                    logger.Info("Lote no recibido");
                    Client.Dispose();
                }

                // definimos udp
                MainView.udp = Udp;
                //Lanzamos el dummy
                //StartDummy();

#if ISDB
                try
                {
                    //Leemos el fichero de configuración que tiene la información de la linea 
                    DB_HandleConfig.LoadLineConfiguration(System.IO.Path.Combine(folderManager.ConfigFolder, "DB", "DB.config.xml"));

                    //Realiza la conexión a la DB
                    logger.Info("Conectando a BBDD");
                    DB = new DB(Path.Combine(folderManager.ConfigFolder, "DB"));
                    ActualizarEstadoDB();
                    //nos suscribimos al evento 
                    MainView.BtnReconectDB.Click += BtnReconectDB_Click;
                    DB.DBConnectionChanged += DB_DBConnectionChanged;

                }
                catch (Exception ex)
                {
                    logger.Error("Error durante la coneción a la base de datos: " + ex);
                }
                //Se comprueba que queramos realizar la conexion a la DB
                //if (Global.IsDBAvailable)
                //{

                //}
#endif

            }
            catch (Exception ex)
            {
                logger.Error(ex);
                if (Client != null)
                {
                    Client.Dispose();
                    logger.Info("Conexión con NiceLabel cerrada.");
                }
#if ISDB
                if (DB.DBConnected)
                {
                    DB.Close();
                }
#endif
            }

        }
        private void DB_DBConnectionChanged(object sender, EventArgs e)
        {
            ActualizarEstadoDB();
        }
        private void server_OnNewClient()
        {
            logger.Info("Cliente conectado");
        }

        /// <summary>
        /// Metodo para liberar recursos antes de cerrar
        /// </summary>
        public override void Disposing()
        {
            try
            {
                Udp.Write("SystemReady", "0");

                logger.Info("SystemReady --> 0");
                server.Dispose();
                logger.Info("Servidor TCP cerrado");

            }
            catch (Exception ex)
            {

                logger.Error(ex);
            }
        }
        #endregion

        #region Methods


        /// <summary>
        /// Metodo para la evaluacion de los resultados
        /// </summary>
        /// <param name="Index">Indice del sistema de vision</param>
        ///<param name="ResultPath">Contenedor de resultados e imagenes. Si el dispositivo es un dataman envia la lectura como ResultPath</param>
        /// <returns>Si devuelve true enviara notificacion para actualizar el hmi con el FinalResult</returns>
        public override Task Evaluate(int Index, string ResultPath)
        {
            return Task.Run(() =>
            {
                try
                {
                    //deserializamos resultado del vpro
                    BcnResultContainer resultContainer = BcnResultContainer.Deserialize(ResultPath);


                    //iniciamos variable local de resultado
                    bool resultInspection = false;

                    //Obtenemos el valor de ToolresultConstant
                    resultInspection = (resultContainer.ToolResultConstant == BcnResultConstants.Accept) ? true : false;

                    //enviamos resultado customizado al PLC, para no esperar a completar todo el AS
                    if (resultInspection) Udp.Write("CamResult_" + Index.ToString("00"), "1");
                    
                    //mostramos log en sentinel
                    logger.Fatal("Resultado_" + Index.ToString("00") + "|" + resultInspection.ToString());

                    //Obtenemos el nombre de la camara
                    string NameVS = BcnConfigFile.Configuration.VisionSystems[Index].Name;

                    //Si existe la salida del VisionPro "ResultString"
                    if (resultContainer.ToolBlockOutputs.Exists(x => x.Name == "ResultString"))
                    {
                        string ResultString = (string)resultContainer.ToolBlockOutputs[0].Value;
                        defectos = new bool[ResultString.Length];
                        //["ResultString"].Value.Length]; // Crear un array de tamaño igual a la longitud de binarioString
                        logger.Info("Result_" + NameVS + " : " + ResultString);

                        //componemos el string de defectos
                        string DefectInfo = string.Join("_", ResultString.Select((c, i) => c == '1' ? $"{(Defecto)i}" : "").Where(s => !string.IsNullOrEmpty(s)));
                                             
                        if (DefectInfo != "")
                        {
                            logger.Fatal("defects_" + Index.ToString("00") + ">>>>" + DefectInfo);
                        }


                        ////Hacemos resize de la imagen original a un tamaño conocido, a una decima parte
                        //List<BcnRawImage> imgs = new List<BcnRawImage>();
                       
                        
                        //deserializamos el img container
                        BcnImageContainer ImagenContainer = BcnImageContainer.Deserialize(ResultPath);

                        //guardamos la imagen con el nombre personalizado (el guardado de CTX está desactivado, ya que sino nos guardaría la imagen con gráficos))
                        SaveImages(ImagenContainer.Images[0], Index, DefectInfo);

                        ImagenContainer.Info = DefectInfo;

                        //creamos una nueva imagen a partir de la original
                        BcnRawImage imgGraphics = new BcnRawImage(ImagenContainer.Images[0]);
                        //la redimensionamos a la mitad
                        imgGraphics.Resize(Convert.ToInt16(ImagenContainer.Images[0].Width / 2), Convert.ToInt16(ImagenContainer.Images[0].Height / 2));
                        //printamos los defectos encontrados
                        
                        // Separamos los defectos en una lista
                        string[] defectosArray = DefectInfo.Split('_');

                        // Posición inicial para el primer texto
                        int startX = 100; 
                        int startY = 100;  
                        int lineSpacing = 50;  // Espaciado entre líneas

                        // printamos cada defecto en una línea separada
                        foreach (string defecto in defectosArray)
                        {
                            imgGraphics = Vidi.DrawText(imgGraphics, defecto, new GraphicSettings(Color.LightSalmon, 3, startX, startY, 2, true));
                            startY += lineSpacing;  // Mover la posición verticalmente para la siguiente línea
                        }
                        //imgGraphics = Vidi.DrawText(imgGraphics, DefectInfo, new GraphicSettings(Color.Yellow, 3, 100, 100, 2, true));
                        //imgs.Add(imgGraphics);
                        //imgs.Add(ImagenContainer.Images[0]);

                        //serializamos la imagen con gráficos redimensionada, para que se muestre ene l HMI
                        ImagenContainer.Images[0] = imgGraphics;
                        BcnImageContainer.Serialize(ResultPath, ImagenContainer);
                        //BcnResultContainer.Serialize(ResultPath, resultContainer);

                    }
                    else
                    {
                        logger.Error("No se puede encontrar una salida del vpro con nombre: ResultString");
                    }
#if ISDB
                    //GESTION BBDD
                    Dictionary<string, float> dict = new Dictionary<string, float>();
                    if (!DB.DBConnected)
                    {
                        ActualizarEstadoDB();
                        logger.Error("Error en la escritura de la BBDD.");
                    }
                    else { 
                        Task.Run(async () =>
                    {
                        try
                        {
                            for (int i = 1; i <= defectos.Length; i++)
                            {
                                dict.Add("Measure" + i.ToString(), (!defectos[i - 1]) ? 0f : 1f);
                            }

                            await DB.AddCycle((defectos.All(elemento => elemento == false) ? GlobalResultEnum.OK : GlobalResultEnum.ERROR), NameVS, dict, Index, ListRecipes[Index]);
                        }
                        catch (Exception ex)
                        {
                            logger.Error("Error en la escritura de la BBDD -->" + ex.Message);
                        }
                    });
#endif                   
                }
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }
            });
        }
        private void BtnReconectDB_Click(object sender, RoutedEventArgs e)
        {
#if ISDB
            if (!DB.DBConnected)
            {
                if ((MessageBox.Show("¿Intentar la reconexión a la BBDD?", "Base de datos", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes))
                {
                    //Realiza la conexión a la DB del modulo estadistico
                    DB = new DB(System.IO.Path.Combine(folderManager.ConfigFolder, "DB"));
                    ActualizarEstadoDB();
                }
            }
#endif
        }
        public void ActualizarEstadoDB()
        {
            MainView.Dispatcher.Invoke(() =>
            {
                MainView.RectStateDB.Fill = (DB.DBConnected) ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Salmon;
                MainView.BtnReconectDB.Visibility = (DB.DBConnected) ? Visibility.Hidden : Visibility.Visible;
            });
        }

        private Task SaveImages(BcnRawImage imageToSave, int Index, string DefectInfo)
        {
            return Task.Run(async () =>
            {
                try
                {
                    string FolderImages = folderManager.ImagesFolder + "\\Linea" +(Index + 1).ToString() + "\\" + DateTime.Now.ToString("yyyyMMdd'_'HHmmssfff").Split('_')[0] + "\\";
                    if (!Directory.Exists(FolderImages)) Directory.CreateDirectory(FolderImages);

                    string pathWithoutExtension = FolderImages + BcnConfigFile.Configuration.VisionSystems[Index].Name + "_" + DateTime.Now.ToString("yyyyMMdd'_'HHmmssfff") + "_" + DefectInfo;
                    imageToSave.ToBitmap().Save(pathWithoutExtension + ".bmp");
                    
                    //// Obtener el codec de imagen para JPG
                    //ImageCodecInfo jpgEncoder = ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    //if (jpgEncoder != null)
                    //{
                    //    // Configurar los parámetros de compresión de la imagen
                    //    EncoderParameters encoderParams = new EncoderParameters(1);
                    //    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 50L); // Ajusta este valor según pruebas

                    //    // Guardar la imagen con la calidad definida
                    //    img.Images[0].ToBitmap().Save(pathWithoutExtension + ".jpg", jpgEncoder, encoderParams);
                    //}
                    //else
                    //{
                    //    // En caso de no encontrar el encoder, guardar sin compresión extra
                    //    img.Images[0].ToBitmap().Save(pathWithoutExtension + ".jpg", ImageFormat.Jpeg);
                    //}


                }
                catch (Exception ex)
                {
                    logger.Error(ex.Message);
                }

            });
        }
        /// <summary>
        /// Metodo para cargar la receteca para el analisis general de resultado
        /// </summary>
        /// <param name="RecipeLoaded"></param>
        public override Task OnLoadRecipe(string RecipeLoaded)
        {
            return Task.Run(() =>
            {
                try
                {
                    //cargo bbdd

#if ISDB
                    //ConfigureBBDD(RecipeLoaded);
#endif

                }
                catch (Exception ex)
                {

                    logger.Error(ex);

                }
            });
        }

        public override void Notify(string Tag, string Value)
        {
            try
            {
                if (Tag.Contains("LoadRecipe_"))
                {
                    ListRecipes[Convert.ToInt16(Tag.Split('_')[1])] = Value.Split('?')[0];
                }
                else if (Tag.Contains("Counters"))
                {

                    int VisSystem = Convert.ToInt16(Tag.Split('_')[1]);
                    logger.Info("CargandoReceta en VS: " + VisSystem.ToString("00"));
                    UpdateLote((VisSystem + 1).ToString("00"), VisSystem.ToString("00"));


                }
            }
            catch (Exception)
            {

                throw;
            }
        }

        /// <summary>
        /// Devuelve la estaciones dónde nos encontramos
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        private int GetStation(int index)
        {
            try
            {
                //Se obtiene el nombre del VisionSystem
                string NameVS = BcnConfigFile.Configuration.VisionSystems[index].Name;

                for (int i = 0; i < VisionStations.Count; i++)
                {
                    //Si la cámara está dentro de la estació, se devuelve el indice
                    if (VisionStations[i].Cameras.FindIndex(x => x.Name == NameVS) != -1) return i;
                }

                return -1;
            }
            catch (Exception)
            {
                return -1;
            }

        }

        public int GetStationOneByOneFolder(int IndexVS)
        {
            foreach (var station in AdvancedScriptConfiguration.Stations)
            {
                var cameraIndices = station.Cameras.Split('|').Select(int.Parse).ToList();

                if (cameraIndices.Contains(IndexVS))
                {
                    return station.ID; // Retorna el ID de la estación
                }
            }

            return -1; // Valor por defecto si no se encuentra la estación
        }
        /// <summary>
        /// Handle Coms PLC
        /// </summary>
        /// <param name="tag"></param>
        /// <param name="value"></param>
        private void HandleComs(string tag, string value)
        {
            //Si no hay VisionStation salimos
            if (VisionStations == null) return;

            //Para controlar la acquisicion de las camaras
            if (tag.Contains("TriggerAck"))
            {
                //Se obtiene el índide de la estación según Índice del VS
                int IndexStation = GetStation(Convert.ToInt16(tag.Split('_')[1]));

                if (IndexStation == -1) return;

                lock (VisionStations[IndexStation])
                {
                    //Se Gestiona el Acquired
                    VisionStations[IndexStation].HandleAcquired(BcnConfigFile.Configuration.VisionSystems[Convert.ToInt16(tag.Split('_')[1])].Name);
                }
            }
            //Si el TAG contiene Cola, se gestiona
            else if (tag.Contains("IDCola"))
            {
                //Se obtiene el índide de la estación según Índice del VS
                int IndexStation = Convert.ToInt16(tag.Split('_')[1]) - 1;

                if (VisionStations.Count == 0) return;

                //Encola las ID's por estación si está activado
                if (VisionStations[IndexStation].Configuration.IDControl && Convert.ToInt16(value) > 0)
                {
                    //HAcemos un lock por si en el momento de encolar estamos desencolando
                    lock (VisionStations[IndexStation].ColaIDs)
                    {
                        VisionStations[IndexStation].ColaIDs.Enqueue(Convert.ToInt16(value));

                        VisionStations[IndexStation].Cameras.ForEach(x => x.SendIDCola(value));
                    }

                }

            }
            //Se gestiona el ResetPorEstacion
            else if (tag.Contains("ResetEst_") && value == "1")
            {
                //Se obtiene el índide de la estación según Índice del VS
                int IndexStation = GetStation(Convert.ToInt16(tag.Split('_')[1]));

                if (IndexStation == -1) return;

                //De todas las camaras y defectos se ponen a false
                VisionStations[IndexStation].Clear(true);
            }
            else if (tag.Contains("ResetAll"))
            {
                //De todas las camaras y defectos se ponen a false
                VisionStations.ForEach(x => x.Clear(true));
            }
            else if (tag.Contains("LoadRecipeCompleted_") && Global.RecipeType == Global.CortexRecipeType.OneByOne)
            {
                LoadRecipeOneByOne(tag.Split('_')[1], value);
            }
        }

        private void GetRecipeID(string tag, string value)
        {
            try
            {

            }
            catch (Exception)
            {

                throw;
            }
        }

        private void LoadRecipeOneByOne(string IndexVS, string RecipeID)
        {

            try
            {
                //Se carga el XML de configuración
                LoadXML(Path.Combine(folderManager.ConfigFolder, "AdvancedScriptConfig.xml"));

                int Index = Convert.ToInt16(IndexVS);
                //int IndexStation = AdvancedScriptConfiguration.Stations[Index].ID;
                int IndexStation = GetStationOneByOneFolder(Index);

                //Se guarda el nombre de los xml en funcion del VS
                string ruta = folderManager.RecipesFolder + "\\" + BcnConfigFile.Configuration.VisionSystems[Index].Name.ToString();
                string[] RecipesXML = Directory.GetFiles(ruta, "*.xml").Select(Path.GetFileNameWithoutExtension).ToArray();

                //obtenemos nombre de la receta
                string RecipeName = ListRecipes[Convert.ToInt16(IndexVS)];/*RecipesXML[Convert.ToInt32(RecipeID) - 1];*/
                int IDRecipe = Convert.ToInt16(RecipeName.Split('-')[0]);

                // Eliminamos la estación con ese ID
                VisionStations.RemoveAll(x => x.ID == IndexStation);

                logger.Error("camIndex" + Index.ToString() + "|   " + "station" + IndexStation.ToString() + "|   ");

                // Volvemos a agregar la estación con el mismo ID que fue eliminado
                VisionStations.Add(new VisionStation(IndexStation, Udp, RecipeName, AdvancedScriptConfiguration.Stations[IndexStation], logger, -1));

                bool res = Global.CheckXMLOK(AdvancedScriptConfiguration);

                if (res) logger.Info("AS Configuration LOADED --> OK"); else logger.Fatal("AS Configuration LOADED --> NOOK");

#if ISDB
                ConfigureBBDD(RecipeName, Index, IDRecipe);
#endif
            }
            catch (Exception)
            {

                throw;
            }
        }

        #endregion

        #region Events
        /// <summary>
        /// Evento nuevo mensaje por udp
        /// </summary>
        /// <param name="Tag">Nombre de la tag</param>
        /// <param name="Value">Valor</param>
        public override void Coms_OnValueChanged(string Tag, string Value)
        {
            Task.Run(() =>
            {
                try
                {
                    //Separamos el mensaje por interrogantes
                    string[] splitValue = Value.Split('?');

                    if (Tag.Contains("TriggerAck"))
                    {
                        //reset señales
                        for (int i = 0; i < VisionStations.Count; i++)
                        {
                            Udp.Write("Nok_0" + i.ToString(), "0");
                            Udp.Write("Ok_0" + i.ToString(), "0");
                            Udp.Write("InspectionComplete_0" + i.ToString(), "0");
                        }


                        //Actualizamos lote del VS correspondiente
                        // UpdateLote(Tag.Split('_')[1]);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }
            });
        }

        private void Server_OnValueChanged(string Tag, string Value)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke((Action)(async () =>
                {
                    //AddLote(Tag, Value);

                    //Se borra el final de linea
                    Regex reg = new Regex("\u0013");
                    string messageWithoutLine = reg.Replace(Value, string.Empty);
                    //string[] message = messageWithoutLine.Split('|');
                    //string valueClean = message[1];
                    //string linea = Tag.Substring(nuevoLote.Length - 2, 2);
                    //AddLote(linea, nuevoLote);
                    AddLote(Tag, Value);
                    //Le pasamos al updatelote el tag y el numero de VS, que sera el numero de linea-1
                    UpdateLote(Tag, (Convert.ToInt16(Tag) - 1).ToString("00"));


                    server?.Write(Tag);
                }));

            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        private void WriteToCsv(bool[] data)
        {
            try
            {
                string csvDate = csvFilePath.Split('.')[0] + "_" + (DateTime.Now.ToString("dd_MM_yyyy")) + "." + csvFilePath.Split('.')[1];
                // Comprobar si existe el archivo CSV
                bool fileExists = File.Exists(csvDate);

                // Abrir el archivo CSV en modo append (añadir al final)
                using (var writer = new StreamWriter(csvDate, true))
                {
                    if (!fileExists)
                    {
                        // Si el archivo no existe, escribir el encabezado
                        writer.WriteLine("fecha;hora;resultado;G1A;G1B;G1C;G1D;G1E;G1F;G1G;G2;G3;G4");
                    }

                    // Escribir los datos en el archivo CSV
                    writer.Write(DateTime.Now.ToString("dd/MM/yyyy")); // Fecha actual
                    writer.Write(";");
                    writer.Write(DateTime.Now.ToString("HH:mm:ss")); // Hora actual
                    if (Array.TrueForAll(data, d => !d))
                    {
                        writer.Write(";OK"); // Si todos los elementos son falsos, resultado = OK
                    }
                    else
                    {
                        writer.Write(";NOK"); // Si algún elemento es verdadero, resultado = NOK
                    }
                    foreach (var value in data)
                    {
                        writer.Write(";" + (value ? "1" : "0")); // Convertir booleanos a "0" o "1"
                    }
                    writer.WriteLine(); // Nueva línea para el próximo registro
                }
            }
            catch (Exception ex)
            {
                // Manejar cualquier error durante la escritura
                Console.WriteLine($"Error al escribir en el archivo CSV: {ex.Message}");
            }
        }
        private void UpdateLote(string Line, string VS)
        {
            if (lineasEnManual.Contains(Line) && lotesManuales.ContainsKey(Line))
            {
                Udp.Write("VisionProInput_" + VS, "SetLote0?" + lotesManuales[Line] + "?");
                logger.Info("[MANUAL] LOTE linea: " + VS + " _" + lotesManuales[Line]);
            }
            else
            {
                var lotesBuffer = GetLotesByLinea(Line);
                for (int i = 0; i < lotesBuffer.Count; i++)
                {
                    Udp.Write("VisionProInput_" + VS, "SetLote" + i + "?" + lotesBuffer[i] + "?");
                    logger.Info("LOTE linea: " + VS + " _" + lotesBuffer[i]);
                }
            }
        }
        public void ActivarLoteManual(string visionSystem, string loteManual)
        {
            lotesManuales[visionSystem] = loteManual;
            lineasEnManual.Add(visionSystem);
            logger.Info($"Lote manual activado para {visionSystem}: {loteManual}");
        }

        public void DesactivarLoteManual(string visionSystem)
        {
            lineasEnManual.Remove(visionSystem);
            lotesManuales.Remove(visionSystem);
            logger.Info($"Lote manual desactivado para {visionSystem}, se usará automático");
        }

        private string GetOCR(BcnVidiToolResult bcnVidiToolResult, int lines)
        {
            List<BcnVidiFeature> listResOCR = bcnVidiToolResult.BcnVidiFeatures.Where(x => x.CogGraphic.GetType().ToString() == "Cognex.VisionPro.CogGraphicLabel").ToList();

            List<BcnVidiFeature> combinedList;
            if (lines > 1)
            {

                List<BcnVidiFeature> line1;
                List<BcnVidiFeature> line2;
                listResOCR = listResOCR.OrderBy(item => item.Y).ToList();
                line1 = listResOCR.Take(12).OrderBy(item => item.X).ToList();
                line2 = listResOCR.Skip(12).OrderBy(item => item.X).ToList();
                combinedList = line1.Concat(line2).ToList();
            }
            else
            {
                combinedList = listResOCR.OrderBy(item => item.X).ToList();
            }


            return string.Join("", combinedList.Select(x => x.Name).ToList());
        }
        private string GetMatchingLote(string readLote)
        {
            lock (lockObject)
            {
                foreach (var (lote, timestamp) in loteBuffer)
                {
                    // Considera lotes en un rango de tiempo o productos específicos
                    if (lote == readLote)
                    {
                        return lote;
                    }
                }
            }
            return null; // No se encontró un lote coincidente
        }

        // Función para añadir un lote nuevo para una línea de producción específica
        public void AddLote(string lineaProduccion, string newLote)
        {
            // Si la línea de producción no está en el diccionario, la añadimos
            if (!lotesPorLinea.ContainsKey(lineaProduccion))
            {
                lotesPorLinea[lineaProduccion] = new List<string>(); // Crear una nueva lista para esa línea
            }

            // Obtener el buffer de lotes actual de la línea de producción
            var lotes = lotesPorLinea[lineaProduccion];

            // Si el buffer ya contiene el lote nuevo, no hacemos nada
            if (lotes.Contains(newLote))
            {
                return; // No agregar el lote si ya está en el buffer
            }

            // Si el buffer ya tiene 2 lotes, eliminamos el más antiguo (el primero en la lista)
            if (lotes.Count >= 2)
            {
                lotes.RemoveAt(0); // Eliminar el lote más antiguo
            }

            // Agregar el nuevo lote al buffer
            lotes.Add(newLote);
        }



        private void Server_OnNewClient()
        {
            try
            {
                logger.Info("Conectado");
            }
            catch (Exception)
            {

                throw;
            }
        }

        private void Server_OnMessageEvent(string Value)
        {

        }
        private void Client_OnMessageEvent(string Value)
        {
            try
            {


                //compruebo si he alcanzado el limite de conexiones
                if (Value.Contains("denegado"))
                {
                    logger.Fatal("LIMITE DE USUARIOS ALCANZADO");
                    Client.Dispose();
                }
                else
                {
                    if (!Value.Contains("HITACHI") && Value != "")
                    {
                        //string linea = Value.Split('|')[0];
                        // Limpiar el valor recibido antes de asignarlo a nuevoLote
                        //string nuevoLote = CleanLote(Value.Split('|')[1].Trim().Replace("hh:mm", "").Replace(" ", "").Replace("\r", "").Replace("\n", ""));

                        /*AddLoteToBuffer(nuevoLote); // Añadir el lote al buffer
                    SetLote = nuevoLote; // Mantenemos SetLote para otras operaciones si es necesario
                    */
                        // Añadir el lote recibido a la línea de producción correspondiente
                        //AddLote(linea, nuevoLote);
                        // Limpiar el valor recibido antes de asignarlo a nuevoLote
                        string nuevoLote = CleanLote(Value.Trim().Replace("hh:mm", "").Replace(" ", "").Replace("\r", "").Replace("\n", ""));
                        /*AddLoteToBuffer(nuevoLote); // Añadir el lote al buffer
                        SetLote = nuevoLote; // Mantenemos SetLote para otras operaciones si es necesario
                        */
                        // Añadir el lote recibido a la línea de producción correspondiente
                        string linea = nuevoLote.Substring(nuevoLote.Length - 2, 2);
                        AddLote(linea, nuevoLote);
                    }
                }
            }
            catch (Exception ex)
            {

                logger.Fatal("Error al recibir mensaje: " + ex);
            }
        }
        /// <summary>
        /// Función para obtener los lotes de una línea de producción
        /// </summary>
        /// <param name="lineaProduccion"></param>
        /// <returns></returns>
        public List<string> GetLotesByLinea(string lineaProduccion)
        {
            if (lotesPorLinea.ContainsKey(lineaProduccion))
            {
                return lotesPorLinea[lineaProduccion];
            }
            else
            {
                return new List<string>(); // Devolver una lista vacía si no hay lotes para esa línea
            }
        }
        private string CleanLote(string lote)
        {
            // Eliminar BOM, caracteres invisibles, signos de interrogación, etc.
            return lote.Trim().Replace("\uFEFF", "").Replace("?", "").Normalize();
        }

        private void AddLoteToBuffer(string lote)
        {
            lock (lockObject)
            {
                // Añadir el lote al buffer con la marca de tiempo actual
                loteBuffer.Enqueue((lote, DateTime.Now));

                // Si se excede la capacidad, elimina el lote más antiguo
                if (loteBuffer.Count > bufferCapacity)
                {
                    loteBuffer.Dequeue();
                }
            }
        }

        #endregion

        #region Private Methods
        /// <summary>
        /// Método para arrancar dummy
        /// </summary>
        public void StartDummy()
        {
            //Dummy
            CTBHandler DummyProcess = new CTBHandler(Path.Combine(FolderManager.ConfigFolder, "Dummy", "Dummy.vpp"));

            try
            {
                IsDummyRunning = true;

                Task.Run(() =>
                {
                    while (IsDummyRunning)
                    {
                        DummyProcess.ToolBlock.Run();
                        System.Threading.Thread.Sleep(200);

                    }

                    logger.Fatal("Dummy Canceled");

                });
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Configura la base de datos con la información del sistema y de la carga de receta
        /// </summary>
        /// <param name = "recipeLoaded" ></ param >
        private void ConfigureBBDD(string recipeLoaded, int IndexVS, int IndexRecipe)
        {
            try
            {
                Task.Run(async () =>
                {
#if ISDB
                    //Se carga el fichero de configuración de la DB por receta
                    DB.LoadRecipe(System.IO.Path.Combine(folderManager.ConfigFolder), BcnConfigFile.Configuration.VisionSystems[IndexVS].Name, recipeLoaded, IndexVS);

                    DB.ManageRecipe(IndexRecipe.ToString(), recipeLoaded, IndexVS);

                    //Se recorre todas las medidas configuradas 
                    foreach (var item in Measure_HandleConfig.DB_RecipeConfig[IndexVS].Measures)
                    {
                        await DB.AddMeasure(item.Id, item.Name, IndexVS, ConfigurationType.Measure, ListRecipes[IndexVS]);
                    }

                    //Se añaden los 5 cams como maximo para las lineas
                    List<string> FiltersValue = new List<string>();
                    for (int i = 1; i <= BcnConfigFile.Configuration.VisionSystems.Count; i++)
                    {
                        FiltersValue.Add("CAM" + i);
                    }

                    //Se añaden los filtros a la DB
                    foreach (var item in DB_HandleConfig.DB_ConfFile.FiltersLine)
                    {
                        await DB.AddFilter(item.Id, item.Name, FiltersValue, DB_HandleConfig.DB_ConfFile.LineInfo.Id);
                    }

#endif
                });


            }
            catch (Exception e)
            {
                logger.Error(e.Message);
            }
        }

        /// <summary>
        /// Carga el XML de configuración del AdfvancedScript
        /// </summary>
        /// <param name="xmlPath"></param>
        private void LoadXML(string xmlPath)
        {
            XmlSerializer xs = new XmlSerializer(typeof(AdvancedScriptConfiguration));
            using (StreamReader sr = new StreamReader(xmlPath))
            {
                try
                {
                    AdvancedScriptConfiguration = (AdvancedScriptConfiguration)xs.Deserialize(sr);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }
        /// <summary>
        /// metodo para la carga del vidi al server
        /// </summary>
        /// <param name="path"></param>
        private void LoadVidiWS(string path)
        {
            try
            {
                Vidi = new BcnVidi();
                Vidi.LoadWorkspace(path);
                //Guardamos el nombre
                string WSName = Path.GetFileNameWithoutExtension(path);

                //Esperamos a que este cargado el workspace
                while (!Vidi.LoadWorkSpaceAck)
                {
                    System.Threading.Thread.Sleep(1000);
                }
                //Lanzamos la carga de ws 5 veces para mejorar velocidad

                //for (int k = 0; k < 5; k++)
                //{
                //    //Se carga el workspace
                //    Vidi.RunStream(Path.Combine(FolderManager.ConfigFolder, "img.bmp"), WSName, "default", 30000, true);


                //}
            }
            catch (Exception ex)
            {

                logger.Error("Error en carga de WS: " + ex);
            }
        }
        #endregion
    }
}
