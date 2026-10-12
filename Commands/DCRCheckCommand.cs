using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MumbaiDCRChecker.UI;
using System;

namespace MumbaiDCRChecker.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DCRCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument uidoc = uiApp.ActiveUIDocument;
                Document doc = uidoc.Document;

                if (doc == null)
                {
                    message = "No document is open. Please open a Revit project first.";
                    return Result.Failed;
                }

                // Launch the WPF UI
                MainWindow window = new MainWindow(doc, uidoc);
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("DCR Check Error", 
                    "An error occurred:\n" + ex.Message + "\n\nStack Trace:\n" + ex.StackTrace);
                return Result.Failed;
            }
        }
    }
}
