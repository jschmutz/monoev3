using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Diagnostics;
using MonoBrickFirmware.Display;
using MonoBrickFirmware.UserInput;
using MonoBrickFirmware.Native;
using MonoBrickFirmware.Display.Menus;
using MonoBrickFirmware.Display.Dialogs;
using MonoBrickFirmware.Settings;
using MonoBrickFirmware.Services;
using MonoBrickFirmware.Connections;

using System.Reflection;
using System.Collections.Generic;
using System.Linq;

namespace StartupApp
{
	class MainClass
	{
		static Bitmap monoLogo = Bitmap.FromResouce(Assembly.GetExecutingAssembly(), "monologo.bitmap");
		static Font font = Font.MediumFont;
		static bool updateProgramList = false;
		static string WpaSupplicantFileName = "/mnt/bootpar/wpa_supplicant.conf";
		static string ProgramPathSdCard = "/mnt/bootpar/apps";
		static string ProgramPathEV3 = "/home/root/apps/";
		static FirmwareSettings settings = new FirmwareSettings();
		static string versionString = "Firmware: 0.1.0.0";
		static string versionURL = "http://www.monobrick.dk/MonoBrickFirmwareRelease/latest/version.txt";
		
		enum ExecutionMode {Normal = 0, Debug = 1, AOT = 2  };
		
		static bool Information()
		{
			string monoVersion = "Unknown";
			Type type = Type.GetType("Mono.Runtime");
			if (type != null)
			{                                          
	    		MethodInfo displayName = type.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static); 
	    		if (displayName != null)                   
	        		monoVersion = (string)displayName.Invoke(null, null); 
			}	
			string monoCLR = System.Reflection.Assembly.GetExecutingAssembly().ImageRuntimeVersion;
			
			Point offset = new Point(0, (int)font.maxHeight);
			Point startPos = new Point(0,0);
			Lcd.Instance.Clear();
			Lcd.Instance.WriteText(font, startPos+offset*0, versionString, true);
			Lcd.Instance.WriteText(font, startPos+offset*1, "Mono version: " + monoVersion.Substring(0,7), true);
			Lcd.Instance.WriteText(font, startPos+offset*2, "Mono CLR: " + monoCLR, true);			
			Lcd.Instance.WriteText(font, startPos+offset*3, "IP: " + WiFiDevice.GetIpAddress(), true);			
			Lcd.Instance.Update();
			Buttons.Instance.GetKeypress();
			return false;
		}
		
		public static bool AOTCompileAndShowDialog(string programFolder)
		{
			List<IStep> steps = new List<IStep> ();
			foreach (string file in Directory.EnumerateFiles(programFolder,"*.*").Where(s => s.EndsWith(".exe") || s.EndsWith(".dll"))) {
				steps.Add (new StepContainer (delegate() {
					return AOTHelper.Compile (file);
				}, new FileInfo(file).Name, "Failed to compile"));
			}
			var dialog = new StepDialog("Compiling",steps);
			return dialog.Show();
		}		
		
		
		static bool ShowProgramOptions (string programFolder)
		{
			string fileName = "";
			try {
				fileName = Directory.EnumerateFiles (programFolder, "*.exe").First ();	
			} catch {
				var info = new InfoDialog (programFolder + "is not executable", true, "Program");
				info.Show ();
				Directory.Delete (programFolder, true);
				updateProgramList = true;
				return true;
			}
			
			var dialog = new SelectDialog<string> (new string[] {
				"Run Program",
				"Debug Program",
				"Run In AOT",
				"AOT Compile",
				"Delete Program",
			}, "Options", true);
			dialog.Show ();
			if (!dialog.EscPressed) {
				Action programAction = null;
				switch (dialog.GetSelectionIndex ()) {
				case 0:
					Lcd.Instance.Clear ();
					Lcd.Instance.DrawBitmap (monoLogo, new Point ((int)(Lcd.Width - monoLogo.Width) / 2, 5));					
					Rectangle textRect = new Rectangle (new Point (0, Lcd.Height - (int)Font.SmallFont.maxHeight - 2), new Point (Lcd.Width, Lcd.Height - 2));
					Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Running...", true, Lcd.Alignment.Center);
					Lcd.Instance.Update ();						
					programAction = () => RunAndWaitForProgram (fileName, ExecutionMode.Normal);	
					break;
				case 1:
					programAction = () => RunAndWaitForProgram (fileName, ExecutionMode.Debug);
					break;
				case 2:
					if (!AOTHelper.IsFileCompiled (fileName)) {
						if (AOTCompileAndShowDialog (programFolder)) {
							programAction = () => RunAndWaitForProgram (fileName, ExecutionMode.AOT);	
						}
					} else {
						programAction = () => RunAndWaitForProgram (fileName, ExecutionMode.AOT);
					}
					break;
				case 3:
						
					if (AOTHelper.IsFileCompiled (fileName)) {
						var questionDialog = new QuestionDialog ("Progran already compiled. Recompile?", "AOT recompile");
						if (questionDialog.Show ()) {
							AOTCompileAndShowDialog (programFolder);
						}
					} else {
						AOTCompileAndShowDialog (programFolder);
					}
					break;
				case 4:
					var question = new QuestionDialog ("Are you sure?", "Delete");
					if (question.Show ()) {
						var step = new StepContainer (() => {
							Directory.Delete (programFolder, true);
							return true;
						}, "Deleting ", "Error deleting program"); 
						var progressDialog = new ProgressDialog ("Program", step);
						progressDialog.Show ();
						updateProgramList = true;
					}
					break;
				}
				if (programAction != null) 
				{
					Console.WriteLine("Starting application");
					programAction();					
					Console.WriteLine ("Done running application");
				}
				return updateProgramList;
			}
			return false;
			
		}
		
		static bool RunPrograms ()
		{
			do
			{
				updateProgramList = false;
				IEnumerable<MenuItemWithAction> itemsFromEV3 = Directory.EnumerateDirectories(ProgramPathEV3)
					.Select((programFolder) => new MenuItemWithAction (new DirectoryInfo(programFolder).Name, () => ShowProgramOptions (programFolder)));
				IEnumerable<MenuItemWithAction> itemsFromSD = Directory.EnumerateDirectories(ProgramPathSdCard)
					.Select ((programFolder) => new MenuItemWithAction (new DirectoryInfo(programFolder).Name, () => ShowProgramOptions (programFolder)));
				
				
				Menu m = new Menu ("Run program:", itemsFromEV3.Concat (itemsFromSD));
				m.Show ();//block
			}while (updateProgramList); 
			return false;
		}
		
		static void RunAndWaitForProgram (string programName, ExecutionMode mode)
		{
			switch (mode) 
			{
				case ExecutionMode.Debug:
					programName = @"--debug --debugger-agent=transport=dt_socket,address=0.0.0.0:" + settings.DebugSettings.Port + ",server=y " + programName;
					System.Diagnostics.Process proc = new System.Diagnostics.Process ();
					Dialog dialog = null;
					Thread escapeThread = null;
					CancellationTokenSource cts = new CancellationTokenSource();
					CancellationToken token = cts.Token;
					string portString  = ("Port: " + settings.DebugSettings.Port).PadRight(6).PadRight(6);
					if (settings.DebugSettings.TerminateWithEscape) {
						escapeThread = new System.Threading.Thread (delegate() {
							while (!token.IsCancellationRequested) {
								if (Buttons.Instance.GetKeypress (token) == Buttons.ButtonStates.Escape) {
									proc.Kill ();
									Console.WriteLine ("Killing process");
									cts.Cancel();
								}
							}
						});
						escapeThread.Start();
						dialog = new InfoDialog (portString + " Press escape to terminate", false, "Debug Mode");
					} 
					else 
					{
						dialog = new InfoDialog (portString + " Waiting for connection.", false, "Debug Mode");	
					}
					dialog.Show ();
					proc.EnableRaisingEvents = false; 
					Console.WriteLine ("Starting process: {0} with arguments: {1}", "/usr/local/bin/mono", programName);
					proc.StartInfo.FileName = "/usr/local/bin/mono";
					proc.StartInfo.Arguments = programName;
					proc.Start ();
					proc.WaitForExit ();
					if (escapeThread != null && !token.IsCancellationRequested) {
						cts.Cancel();
						escapeThread.Join();
					}
					break;
				case ExecutionMode.Normal:
					ProcessHelper.RunAndWaitForProcess("/usr/local/bin/mono", programName); 
					break;
				case ExecutionMode.AOT:
					ProcessHelper.RunAndWaitForProcess("/usr/local/bin/mono", "--full-aot " + programName);	
					break;
			}
		}

		static bool Shutdown ()
		{
			var dialog = new QuestionDialog ("Are you sure?", "Shutdown EV3");
			if(dialog.Show ()){
				Lcd.Instance.Clear();
				Lcd.Instance.WriteText(font, new Point(0,0), "Shutting down...", true);
				Lcd.Instance.Update();
			
				Buttons.Instance.LedPattern(2);
				ProcessHelper.RunAndWaitForProcess("/sbin/shutdown", "-h now");
				Thread.Sleep(120000);
			} 
			return false;
		}
		
		static void ShowMainMenu()
		{
			
			List<IMenuItem> items = new List<IMenuItem>();
			items.Add (new MenuItemWithAction("Programs", () => RunPrograms(),MenuItemSymbole.RightArrow));
			items.Add (new MenuItemWithAction("WiFi Connection", () => ShowWiFiMenu(), MenuItemSymbole.RightArrow));
			//items.Add (new MenuItemWithAction("WebServer", () => ShowWebServerMenu(), MenuItemSymbole.RightArrow));
			items.Add (new MenuItemWithAction("Settings", () => ShowSettings(), MenuItemSymbole.RightArrow));
			items.Add (new MenuItemWithAction("Information", () => Information()));
			items.Add (new MenuItemWithAction("Check for Updates", () => ShowUpdatesDialogs()));
			items.Add (new MenuItemWithAction("Shutdown", () => Shutdown()));
			Menu m = new Menu("Main menu", items);
			m.Show();
		}
		
		static bool ShowWebServerMenu ()
		{
			List<IMenuItem> items = new List<IMenuItem> ();
			var portItem = new MenuItemWithNumericInput("Port", settings.WebServerSettings.Port, 1, ushort.MaxValue);
			portItem.OnValueChanged+= delegate(int value) 
			{
				new Thread(delegate() {
			    	settings.WebServerSettings.Port = value;
					settings.Save();
				}).Start();
			};
			var startItem = new MenuItemWithCheckBox("Start server", WebServer.IsRunning(),
				delegate(bool running)
       	 		{ 
					
					bool isRunning = running;
					if(running){
						var step = new StepContainer(
							delegate() 
							{
								WebServer.StopAll();
								System.Threading.Thread.Sleep(2000);
								return true;
							},
							"Stopping", "Failed to stop");
						var dialog = new ProgressDialog("Web Server",step);
						dialog.Show();
						isRunning = WebServer.IsRunning();
					}
					else{
						var step1 = new StepContainer(()=>{return WebServer.StartFastCGI();}, "Init CGI Server", "Failed to start CGI Server");
						var step2 = new StepContainer(()=>{return WebServer.StartLighttpd();}, "Initializing", "Failed to start server");
						var step3 = new StepContainer(()=>{return WebServer.LoadPage();}, "Loading page", "Failed to load page");
						var stepDialog = new StepDialog("Web Server", new List<IStep>{step1,step2,step3}, "Webserver started");
						isRunning = stepDialog.Show();
					}
					return isRunning;
       			} 
			);
			
			//items.Add(portItem);
			items.Add(startItem);
			//Show the menu
			Menu m = new Menu ("Web Server", items);
			m.Show ();
			return false;
		}
		
		static bool ShowWiFiMenu ()
		{
			List<IMenuItem> items = new List<IMenuItem> ();
			var ssidItem = new MenuItemWithCharacterInput("SSID", "Enter SSID", settings.WiFiSettings.SSID);
			ssidItem.OnDialogExit += delegate(string text) {
				new Thread(delegate() {
		    		settings.WiFiSettings.SSID = text;
					settings.Save();
			    }).Start();
			};
			var passwordItem = new MenuItemWithCharacterInput("Password", "Password", settings.WiFiSettings.Password, true);
			passwordItem.OnDialogExit += delegate(string text) {
				new Thread(delegate() {
			    	settings.WiFiSettings.Password = text;
					settings.Save();
			    }).Start();
			};
			var encryptionItem = new MenuItemWithOptions<string>("Encryption", new string[]{"None","WPA/2"}, settings.WiFiSettings.Encryption ? 1 : 0);
			encryptionItem.OnOptionChanged += delegate(string newOpstion) {
				new Thread(delegate() {
		    		if(newOpstion == "None")
						settings.WiFiSettings.Encryption = false;
					else
						settings.WiFiSettings.Encryption = true;
					settings.Save(); 
			    }).Start();
			};
			var connectItem = new MenuItemWithCheckBox("Connect", WiFiDevice.IsLinkUp(),
				delegate(bool WiFiOn)
         		{ 
					bool isOn = WiFiOn;
					var createFileStep = new StepContainer( 
						delegate() 
						{
							WriteWpaSupplicantConfiguration(settings.WiFiSettings.SSID,settings.WiFiSettings.Password,settings.WiFiSettings.Encryption);
							return true;
						},
						"Creating file", "Error creating WPA file");
					var progressDialog = new ProgressDialog("WiFi", createFileStep);
					progressDialog.Show();
					if(WiFiOn){
						var turnOffStep = new StepContainer( 
						delegate() 
						{
							WiFiDevice.TurnOff();
							return true;
						},
						"Turning Off","Error turning off WiFi","WiFi Disabled");
						var dialog = new ProgressDialog("WiFi", turnOffStep);
						dialog.Show();
						isOn = false;
					}
					else{
						var turnOnStep = new StepContainer( 
						delegate() 
						{
							return WiFiDevice.TurnOn(60000);
						},
						"Connecting", "Failed to connect");
						Dialog dialog = new ProgressDialog("WiFi", turnOnStep);
						if(dialog.Show()){
							if(settings.WiFiSettings.ConnectAtStartUp == false){
								var question = new QuestionDialog("Do you want to connect at start-up?", "Settings");
								if(question.Show()){
									new Thread(delegate() {
								    	settings.WiFiSettings.ConnectAtStartUp = true;
										settings.Save();
								    }).Start();
								}
							}
							dialog = new InfoDialog("Connected Successfully " + WiFiDevice.GetIpAddress(), true, "WiFi");
							dialog.Show();
							isOn = true;
						}
						else{
							isOn = false;
						}
					}
					return isOn;
         		} 
			);
			items.Add(ssidItem);
			items.Add(passwordItem);
			items.Add(encryptionItem);
			items.Add(connectItem);
			//Show the menu
			Menu m = new Menu ("WiFi Connection", items);
			m.Show ();
			return false;	
		}

		
		static bool ShowSettings ()
		{
			//Create the settings items and apply the settings 
			List<IMenuItem> items = new List<IMenuItem> ();
			var terminateWithEscapeItem = new MenuItemWithCheckBox("Debug termination",settings.DebugSettings.TerminateWithEscape);
			var debugPortItem = new MenuItemWithNumericInput("Debug port",settings.DebugSettings.Port,1, ushort.MaxValue);
			var checkForUpdate = new MenuItemWithCheckBox("Update check",settings.GeneralSettings.CheckForSwUpdatesAtStartUp);
			var wifiConnect = new MenuItemWithCheckBox("WiFi auto connect",settings.WiFiSettings.ConnectAtStartUp);
			var soundVolume = new MenuItemWithNumericInput("Volume",settings.SoundSettings.Volume);
			var enableSound = new MenuItemWithCheckBox("Enable sound", settings.SoundSettings.EnableSound);
			
			
			items.Add(wifiConnect);
			items.Add(checkForUpdate);
			items.Add(terminateWithEscapeItem);
			items.Add (debugPortItem);
			items.Add(soundVolume);
			
			//Show the menu
			Menu m = new Menu ("Settings", items);
			m.Show ();
			new Thread(delegate() {
	    		settings.DebugSettings.TerminateWithEscape = terminateWithEscapeItem.Checked; 
				settings.DebugSettings.Port = debugPortItem.Value;
				settings.GeneralSettings.CheckForSwUpdatesAtStartUp = checkForUpdate.Checked;
				settings.WiFiSettings.ConnectAtStartUp = wifiConnect.Checked;
				settings.SoundSettings.Volume = soundVolume.Value;
				settings.SoundSettings.EnableSound = enableSound.Checked;
				settings.Save();
			}).Start();
			return false;
		}
		
		
		public static void WriteWpaSupplicantConfiguration (string ssid, string password, bool useEncryption)
		{
			MonoBrickFirmware.Native.ProcessHelper.RunAndWaitForProcess("rm",WpaSupplicantFileName); 
			string encryption;
			if(useEncryption)
				encryption = "WPA-PSK";
			else
				encryption = "NONE";	             
			string[] lines = { 
				"#This file is auto generated by MonoBrick - will be overwritten at startup", 
				"#ap_scan=1", 
				"#fast_reauth=1",
				"# Pers", 
				"ctrl_interface=/var/run/wpa_supplicant", 
				"network={", 
				"  ssid=\"" + ssid + "\"",
				"  key_mgmt="+encryption, 
				"  psk=\"" + password + "\"", 
				"  pairwise=CCMP TKIP",
				"  group=CCMP TKIP", 
				"}", 
			};
      		System.IO.File.WriteAllLines(@WpaSupplicantFileName, lines);
		}
		
		static bool ShowUpdatesDialogs ()
		{
			if (WiFiDevice.IsLinkUp()) {
				var step = new StepContainer(
					delegate() 
					{
						try{
							return UpdateAvailable ();
						}
						catch
						{
							return false;
						}
					},
					"Checking server", "No software updates available", "Software update available. Visit monobrick.dk"); 
				var dialog = new ProgressDialog("Updates", step);
				dialog.Show();
			} 
			else 
			{
				var dialog = new InfoDialog ("WiFi device is not pressent", true);
				dialog.Show();	
			}
			return false;
		}
		
		
		public static bool UpdateAvailable ()
		{
			var textFromFile = (new WebClient ()).DownloadString (versionURL).TrimEnd (new char[] { '\r', '\n' });
			return versionString != textFromFile;
		}
		
		
		public static void Main (string[] args)
		{
			Lcd.Instance.DrawBitmap (monoLogo, new Point ((int)(Lcd.Width - monoLogo.Width) / 2, 5));					
			Rectangle textRect = new Rectangle (new Point (0, Lcd.Height - (int)Font.SmallFont.maxHeight - 2), new Point (Lcd.Width, Lcd.Height - 2));
			
			Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Initializing...", true, Lcd.Alignment.Center);
			Lcd.Instance.Update ();						
			WiFiDevice.TurnOff ();
			if (!Directory.Exists (ProgramPathSdCard))
				Directory.CreateDirectory (ProgramPathSdCard);
			
			// JIT work-around remove when JIT problem is fixed
			System.Threading.Thread.Sleep (10);
			Console.WriteLine ("JIT workaround - please remove!!!");
			Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Checking WiFi...", true, Lcd.Alignment.Center);
			Lcd.Instance.Update ();						
			//WiFiDevice.IsLinkUp ();
			Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Starting Mono Runtime...", true, Lcd.Alignment.Center);
			Lcd.Instance.Update ();						
			string monoVersion = "Unknown";
			Type type = Type.GetType ("Mono.Runtime");
			if (type != null) {                                          
				MethodInfo displayName = type.GetMethod ("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static); 
				if (displayName != null)
					monoVersion = (string)displayName.Invoke (null, null);
				Console.WriteLine ("Mono Version" + monoVersion); 
			}	
			string monoCLR = System.Reflection.Assembly.GetExecutingAssembly ().ImageRuntimeVersion;
			// JIT work-around end but look for more below
			
			//Load settings
			Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Loading settings...", true, Lcd.Alignment.Center);
			Lcd.Instance.Update ();
			settings = settings.Load();						
			if (settings != null) {
				Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Applying settings...", true, Lcd.Alignment.Center);
				Lcd.Instance.Update ();						
				settings.Save();// JIT work-around
      			WriteWpaSupplicantConfiguration(settings.WiFiSettings.SSID, settings.WiFiSettings.Password, settings.WiFiSettings.Encryption);
				if (settings.WiFiSettings.ConnectAtStartUp) {
					Lcd.Instance.WriteTextBox (Font.SmallFont, textRect, "Connecting to WiFi...", true, Lcd.Alignment.Center);
					Lcd.Instance.Update ();						
					if (WiFiDevice.TurnOn (60000)) {
						WiFiDevice.GetIpAddress ();// JIT work-around
          				if (settings.GeneralSettings.CheckForSwUpdatesAtStartUp)
          				{
							ShowUpdatesDialogs ();
						} 
						else 
						{
							var dialog = new InfoDialog ("Connected Successfully " + WiFiDevice.GetIpAddress (), true);
							dialog.Show ();
						} 
					} 
					else
					{
						var dialog = new InfoDialog ("Failed to connect to WiFI Network", true);
						dialog.Show ();
					}
				}
			} 
			else 
			{
				var dialog = new InfoDialog ("Failed to load settings", true);
				dialog.Show ();
      			settings = new FirmwareSettings();
			}
			
			//Keep showing the menu even if user press esc
			while(true)
			{
				ShowMainMenu();
			}
						
		}
	}
}
