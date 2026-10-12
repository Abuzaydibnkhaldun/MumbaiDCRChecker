using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Utilities
{
    public class ElementHighlighter
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;

        public ElementHighlighter(Document doc, UIDocument uidoc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _uidoc = uidoc ?? throw new ArgumentNullException(nameof(uidoc));
        }

        public void HighlightViolations(List<ComplianceResult> results)
        {
            try
            {
                // STEP 1: Get or create 3D view BEFORE starting transaction
                View3D view3D = Get3DView();
                if (view3D != null)
                {
                    _uidoc.ActiveView = view3D;
                }
                else
                {
                    TaskDialog.Show("Highlight Error", "Could not create or find a 3D view.");
                    return;
                }

                // STEP 2: Collect ONLY failed violation elements
                List<ElementId> failedIds = new List<ElementId>();

                foreach (var result in results)
                {
                    if (result.Status == "FAIL" &&
                        result.ViolatingElementIds != null && result.ViolatingElementIds.Length > 0)
                    {
                        failedIds.AddRange(result.ViolatingElementIds);
                    }
                }

                if (failedIds.Count == 0)
                {
                    TaskDialog.Show("Highlight Complete", 
                        "No violations to highlight — all checks passed or have warnings only.");
                    return;
                }

                // STEP 3: Apply red overrides to failed elements only
                using (Transaction trans = new Transaction(_doc, "Highlight DCR Violations"))
                {
                    trans.Start();

                    try
                    {
                        OverrideGraphicSettings failSettings = new OverrideGraphicSettings();
                        failSettings.SetProjectionLineColor(new Color(255, 0, 0)); // Red
                        failSettings.SetProjectionLineWeight(8);
                        failSettings.SetSurfaceTransparency(30);
                        // Also color the surface fill for better visibility
                        failSettings.SetSurfaceForegroundPatternColor(new Color(255, 80, 80));

                        var distinctIds = failedIds.Distinct().ToList();
                        foreach (var id in distinctIds)
                        {
                            try
                            {
                                _uidoc.ActiveView.SetElementOverrides(id, failSettings);
                            }
                            catch { /* Element may not be visible in view */ }
                        }

                        trans.Commit();

                        TaskDialog.Show("Highlight Complete",
                            string.Format("Highlighted {0} element(s) with violations (RED).\n\n" +
                                         "Elements include walls, stairs, railings, floor slabs,\n" +
                                         "and other elements that failed compliance checks.",
                                         distinctIds.Count));
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        throw new InvalidOperationException($"Failed to highlight violations: {ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Highlight Error", "Error highlighting violations:\n" + ex.Message);
            }
        }

        private View3D Get3DView()
        {
            FilteredElementCollector collector = new FilteredElementCollector(_doc);
            var view3D = collector.OfClass(typeof(View3D))
                                 .Cast<View3D>()
                                 .FirstOrDefault(v => !v.IsTemplate && v.Name.Contains("3D"));

            if (view3D == null)
            {
                using (Transaction trans = new Transaction(_doc, "Create 3D View"))
                {
                    trans.Start();
                    
                    try
                    {
                        ViewFamilyType viewFamilyType = new FilteredElementCollector(_doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

                        if (viewFamilyType != null)
                        {
                            view3D = View3D.CreateIsometric(_doc, viewFamilyType.Id);
                            view3D.Name = "DCR Compliance Check";
                        }
                        
                        trans.Commit();
                    }
                    catch
                    {
                        trans.RollBack();
                    }
                }
            }

            return view3D;
        }
    }
}
