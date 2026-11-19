using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Workspace;
using BambuFilamentImporter.Services;

internal static class Program
{
 [STAThread]
 static int Main(string[] args)
 {
  var repo = args.Length > 0 ? Path.GetFullPath(args[0]) : @"C:\BL Filament Importer";
  var root = Path.Combine(Path.GetTempPath(), "BfiPreviewTest-" + Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  var workspace = new WorkspaceContext(root);
  var liveBefore = WorkspaceContext.Fingerprint(new BambuPaths());
  var paths = workspace.Paths;
  Directory.CreateDirectory(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"));
  var manifest = new JsonObject { ["filament_list"] = new JsonArray() };
  foreach (var family in new[] { "abs", "pet", "petg", "pla" })
  {
   var name = "fdm_filament_" + family;
   File.WriteAllText(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament", name + ".json"),
    new JsonObject { ["name"] = name, ["type"] = "filament", ["filament_type"] = new JsonArray(family is "pet" or "petg" ? "PETG" : family.ToUpperInvariant()), ["hot_plate_temp"] = new JsonArray("70") }.ToJsonString());
   manifest["filament_list"]!.AsArray().Add(new JsonObject { ["name"] = name, ["sub_path"] = "filament/" + name + ".json" });
  }
  File.WriteAllText(Path.Combine(paths.RoamingProfileRoot, "BBL.json"), manifest.ToJsonString());
  File.WriteAllText(paths.ConfigPath, """{"app":{"preset_folder":"test-user"},"filaments":[],"presets":{"filaments":[]}}""");
  var overture = new ProductChoice { PackagePath = Path.Combine(repo,"packages","manufacturers","Overture.bflib"), ProfileName = "Overture ABS @base", Manufacturer = "Overture", Material = "ABS" };
  var sunlu = new ProductChoice { PackagePath = Path.Combine(repo,"packages","manufacturers","SUNLU.bflib"), ProfileName = "SUNLU High Speed Matte PETG @base", Manufacturer = "SUNLU", Material = "PETG" };
  var printer = Target("0.4");
  try
  {
   UpdateNotificationTests.Run(root);
   Throws<InvalidOperationException>(()=>ImportPlan.Create(paths,[],[printer],ImportDestination.Both),"empty selection blocked");
   Throws<InvalidOperationException>(()=>ImportPlan.Create(paths,[overture],[],ImportDestination.Both),"no printer blocked");
   var first = ImportPlan.Create(paths,[overture,sunlu],[printer],ImportDestination.Both);
   Check(first.HasChanges && first.SelectedProducts==2,"multi-manufacturer review");
   var install=first.Apply(workspace);
   Check(install.WrittenFiles.Count==4 && install.ProjectPresetEntriesAdded.Count==2,"multi-manufacturer install into both destinations");
   Check(Directory.EnumerateFiles(workspace.BackupFolder,"*.bflbackup").Any(),"complete automatic backup");
   var library=new BambuLibraryScanner(paths).LoadCurrentFilaments();
   var child=library.Single(p=>p.Name=="Overture ABS @BBL P1S");
   var json=BambuJson.Parse(File.ReadAllText(child.ProfilePath))!.AsObject();
   json["filament_flow_ratio"]=new JsonArray("0.91");json["nozzle_temperature"]=new JsonArray("253");
   File.WriteAllText(child.ProfilePath,json.ToJsonString());
   var extended=ImportPlan.Create(paths,[overture],[Target("0.4","0.6")],ImportDestination.Both);
   Check(extended.Changes.Any(c=>c.Action.StartsWith("Extend")),"partial coverage is explicit");
   extended.Apply(workspace);
   json=BambuJson.Parse(File.ReadAllText(child.ProfilePath))!.AsObject();
   Check(json["filament_flow_ratio"]![0]!.GetValue<string>()=="0.91" && json["nozzle_temperature"]![0]!.GetValue<string>()=="253","extending coverage preserves custom tuning");
   Check(json["compatible_printers"]!.AsArray().Count==4,"P1 aliases and old nozzles retained");
   Check(!ImportPlan.Create(paths,[overture],[Target("0.4","0.6")],ImportDestination.Both).HasChanges,"repeat Both import is a no-op");
   var stale=ImportPlan.Create(paths,[sunlu],[Target("0.8")],ImportDestination.DeviceAms);
   File.AppendAllText(paths.ConfigPath,Environment.NewLine);
   Throws<InvalidOperationException>(()=>stale.Apply(workspace),"stale library review blocked");
   var snapshot=WorkspaceContext.Fingerprint(paths);
   Throws<IOException>(()=>LibraryTransaction.Run<bool>(workspace,"rollback-test",()=>
   {
    File.WriteAllText(child.ProfilePath,"{}");
    File.WriteAllText(Path.Combine(paths.RoamingProfileRoot,"BBL","filament","new-test.json"),"{}");
    throw new InvalidOperationException("simulated failure");
   }),"failed transaction reports rollback");
   Check(snapshot==WorkspaceContext.Fingerprint(paths),"rollback restores every changed catalog/config file");
   var userPlan=ImportPlan.Create(paths,[overture],[printer],ImportDestination.ProjectLibrary);
   userPlan.Apply(workspace);
   var user=new BambuLibraryScanner(paths).LoadCurrentFilaments().Single(p=>p.StorageKind==FilamentStorageKind.UserPreset);
   var settingsService=new FilamentSettingsService(paths);
   var settings=settingsService.Load(user);
   var nozzle=settings.Single(s=>s.Key=="nozzle_temperature");nozzle.Value="249";
   LibraryTransaction.Run(workspace,"settings-test",()=>settingsService.Save(user,settings));
   ImportPlan.Create(paths,[overture],[Target("0.4","0.6")],ImportDestination.ProjectLibrary).Apply(workspace);
   Check(BambuJson.Parse(File.ReadAllText(user.ProfilePath))!["nozzle_temperature"]![0]!.GetValue<string>()=="249","user preset coverage extension preserves overrides");
   Check(!ImportPlan.Create(paths,[overture],[Target("0.4","0.6")],ImportDestination.ProjectLibrary).HasChanges,"project-only import is idempotent");
   var backup=workspace.CreateBackup("restore-test");
   LibraryTransaction.Run(workspace,"remove-test",()=>new BambuLibraryEditor(paths).Remove(user));
   Check(!File.Exists(user.ProfilePath),"profile removal");
   LibraryTransaction.Run(workspace,"restore-test",()=>new LibraryBackupService(paths,()=>false).Restore(backup));
   Check(File.Exists(user.ProfilePath),"backup restoration");
   CustomLibraryTests.Run(workspace, [overture,sunlu], printer);
   StudioRecoveryTests.Run(workspace);
   BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
   Check(liveBefore==WorkspaceContext.Fingerprint(new BambuPaths()),"live library unchanged throughout write tests");
   Render(repo,workspace);
   Console.WriteLine("All workspace checks passed.");
   return 0;
  }
  catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally
  {
   var full=Path.GetFullPath(root);
   if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("BfiPreviewTest-"))Directory.Delete(full,true);
  }
 }
 static PrinterTarget Target(params string[] nozzles)=>new(){Vendor="BBL",ModelName="Bambu Lab P1S",ProfileSuffix="BBL P1S",MachinePresetNames=nozzles.Select(n=>"Bambu Lab P1S "+n+" nozzle").ToList()};
 static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);}
 static void Throws<T>(Action action,string name)where T:Exception{try{action();}catch(T){Check(true,name);return;}throw new Exception("FAIL "+name);}
 static void Render(string repo,WorkspaceContext workspace)
 {
  var app=new BambuFilamentImporter.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };app.InitializeComponent();
  var folder=Path.Combine(repo,"Tests","artifacts","preview");Directory.CreateDirectory(folder);
  foreach(var dark in new[]{false,true})
  {
   ThemeService.Apply(dark);
   var window=new WorkspaceWindow(workspace){InitializeOnLoad=false,Width=1280,Height=900};
   window.Show();window.UpdateLayout();
   Check(window.FindName("TestMode") is null && window.FindName("LiveMode") is null && window.FindName("ResetTestButton") is null,"test/live controls are absent");
   Check(((CheckBox)window.FindName("UseCustomCheck")).IsChecked != true,"custom library opt-in starts unchecked");
   var tabs=(TabControl)window.FindName("Navigation");
   Check(((TabItem)tabs.Items[0]).Header.ToString()=="Add Filaments" && ((TabItem)tabs.Items[1]).Header.ToString()=="My Library","navigation uses requested capitalization");
   var selectedTab=(TabItem)tabs.Items[0];
   var selectedBorder=(Border)selectedTab.Template.FindName("TabBorder",selectedTab);
   var selectionColor=((SolidColorBrush)ThemeService.Brush("SelectionBrush")).Color;
   ((TextBox)window.FindName("ProductSearch")).Focus();window.UpdateLayout();
   Check(((SolidColorBrush)selectedBorder.Background).Color==selectionColor,"selected tab retains highlight when focus moves into search");
   var displayProducts = new List<ProductChoice> {
    new() { PackagePath="demo-overture",ProfileName="Overture ABS @base",Manufacturer="Overture",Material="ABS",Coverage="System: 2 profiles" },
    new() { PackagePath="demo-sunlu",ProfileName="SUNLU High Speed Matte PETG @base",Manufacturer="SUNLU",Material="PETG",Coverage="System: 2 profiles" }
   };
   var displayTree=FilamentTree.Catalog(displayProducts);
   foreach(var node in FilamentTree.Flatten(displayTree))node.IsExpanded=true;
   ((TreeView)window.FindName("ProductsTree")).ItemsSource=displayTree;
   ((ItemsControl)window.FindName("PrinterChoices")).ItemsSource=new[]{new PrinterChoice(Target("0.4","0.6"))};
   ((ComboBox)window.FindName("VendorFilter")).ItemsSource=new[]{"All manufacturers"};
   ((ComboBox)window.FindName("VendorFilter")).SelectedIndex=0;
   ((TreeView)window.FindName("ProductsTree")).ItemsSource=displayTree;
   ((TextBlock)window.FindName("BuildLabel")).Text=$"Version {UpdateService.CurrentVersion.ToString(3)} · Compact workspace · By Zataralee";
   ((TextBlock)window.FindName("StatusText")).Text="Workspace ready.";
   window.UpdateLayout();
   var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
   var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
   using(var stream=File.Create(Path.Combine(folder,dark?"preview-dark.png":"preview-light.png")))png.Save(stream);
   window.Width=1020;window.Height=720;
   ((TextBlock)window.FindName("AppUpdateText")).Text="BFI 0.7.0 is available. Your libraries and settings are kept when updating.";
   ((TextBlock)window.FindName("LibraryUpdateText")).Text="2 downloaded filament library updates available · 1 new filament library available";
   ((Border)window.FindName("AppUpdateBanner")).Visibility=Visibility.Visible;
   ((Border)window.FindName("LibraryUpdateBanner")).Visibility=Visibility.Visible;
   window.UpdateLayout();
   var banner=(Border)window.FindName("LibraryUpdateBanner");
   var navigation=(TabControl)window.FindName("Navigation");
   Check(banner.ActualHeight>=36 && navigation.ActualHeight>200,"update banners fit minimum window in "+(dark?"dark":"light")+" theme");
   var addScroll=(ScrollViewer)window.FindName("AddScroll");
   var review=(Button)window.FindName("ReviewButton");
   Check(addScroll.ScrollableHeight>0 && review.ActualHeight>=36,"small window keeps installation controls reachable by scrolling");
   var noticeImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);noticeImage.Render(window);
   var noticePng=new PngBitmapEncoder();noticePng.Frames.Add(BitmapFrame.Create(noticeImage));
   using(var stream=File.Create(Path.Combine(folder,dark?"notices-dark.png":"notices-light.png")))noticePng.Save(stream);
   window.Close();
  }
  Check(true,"light/dark native window renders with application icon");
 }
}
