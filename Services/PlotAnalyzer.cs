using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    public class PlotAnalyzer
    {
        private readonly Document _doc;
        private const double FEET_TO_METERS = 0.3048;

        public PlotAnalyzer(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public CurveLoop GetPlotBoundary()
        {
            List<Curve> curves = new List<Curve>();

            try
            {
                // Step 1: Collect property line segment elements
                FilteredElementCollector segCollector = new FilteredElementCollector(_doc);
                List<Element> segments = segCollector
                    .OfCategory(BuiltInCategory.OST_SitePropertyLineSegment)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (segments.Count == 0)
                {
                    TaskDialog.Show("Plot Boundary Error",
                        "No property lines found in the model.\n\n" +
                        "Please create property lines using:\n" +
                        "Massing & Site tab → Property Line");
                    return null;
                }

                // Step 2: Try every possible way to get curves
                curves = TryExtractAllCurves(segments);

                // Step 3: If still nothing, show helpful message
                if (curves.Count == 0)
                {
                    string msg = string.Format(
                        "VERSION 3 - Found {0} property line elements but could not extract curves.\n\n" +
                        "WORKAROUND - Please do the following:\n\n" +
                        "1. Go to Architecture tab\n" +
                        "2. Click 'Model Line'\n" +
                        "3. Draw lines around your plot boundary (closed rectangle)\n" +
                        "4. Run the plugin again\n\n" +
                        "The plugin will detect these model lines as the plot boundary.",
                        segments.Count);

                    TaskDialog.Show("Plot Boundary Error", msg);
                    return null;
                }

                // Step 4: Filter valid curves
                List<Curve> validCurves = curves
                    .Where(c => c != null && c.Length > 0.01)
                    .ToList();

                if (validCurves.Count < 3)
                {
                    TaskDialog.Show("Plot Boundary Error",
                        string.Format("Only {0} valid curves found. Need at least 3.", validCurves.Count));
                    return null;
                }

                // Step 5: Sort into a continuous loop
                List<Curve> sortedCurves = SortCurvesIntoContinuousLoop(validCurves);

                if (sortedCurves == null || sortedCurves.Count < 3)
                {
                    TaskDialog.Show("Plot Boundary Error",
                        "Property lines do not form a closed boundary.\n" +
                        "Please ensure all segments connect end-to-end.");
                    return null;
                }

                // Step 6: Create and return the CurveLoop
                CurveLoop plotLoop = CurveLoop.Create(sortedCurves);
                return plotLoop;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Plot Boundary Error", "Error: " + ex.Message);
                return null;
            }
        }

        private List<Curve> TryExtractAllCurves(List<Element> elements)
        {
            List<Curve> curves = new List<Curve>();

            foreach (Element elem in elements)
            {
                // Attempt 1: LocationCurve
                LocationCurve locCurve = elem.Location as LocationCurve;
                if (locCurve != null)
                {
                    curves.Add(locCurve.Curve);
                    continue;
                }

                // Attempt 2: LocationPoint (some line elements store as point pairs)
                LocationPoint locPoint = elem.Location as LocationPoint;

                // Attempt 3: Geometry with all detail levels
                bool foundGeom = false;
                foreach (ViewDetailLevel lvl in new[] {
                    ViewDetailLevel.Fine,
                    ViewDetailLevel.Medium,
                    ViewDetailLevel.Coarse })
                {
                    Options opt = new Options();
                    opt.DetailLevel = lvl;
                    opt.ComputeReferences = false;
                    opt.IncludeNonVisibleObjects = true;

                    GeometryElement geomElem = elem.get_Geometry(opt);
                    if (geomElem == null) continue;

                    List<Curve> extracted = new List<Curve>();
                    ExtractCurvesFromGeometry(geomElem, extracted);

                    if (extracted.Count > 0)
                    {
                        curves.AddRange(extracted);
                        foundGeom = true;
                        break;
                    }
                }

                if (foundGeom) continue;

                // Attempt 4: Dependent elements
                ICollection<ElementId> depIds = elem.GetDependentElements(null);
                foreach (ElementId depId in depIds)
                {
                    Element dep = _doc.GetElement(depId);
                    if (dep == null || dep.Id == elem.Id) continue;

                    LocationCurve depLoc = dep.Location as LocationCurve;
                    if (depLoc != null)
                    {
                        curves.Add(depLoc.Curve);
                        continue;
                    }

                    Options depOpt = new Options();
                    depOpt.DetailLevel = ViewDetailLevel.Fine;
                    depOpt.IncludeNonVisibleObjects = true;
                    GeometryElement depGeom = dep.get_Geometry(depOpt);
                    if (depGeom != null)
                    {
                        ExtractCurvesFromGeometry(depGeom, curves);
                    }
                }
            }

            return curves;
        }

        private void ExtractCurvesFromGeometry(GeometryElement geomElem, List<Curve> curves)
        {
            if (geomElem == null) return;

            foreach (GeometryObject obj in geomElem)
            {
                if (obj == null) continue;

                // Direct curve
                Curve curve = obj as Curve;
                if (curve != null)
                {
                    curves.Add(curve);
                    continue;
                }

                // Geometry instance - recurse
                GeometryInstance inst = obj as GeometryInstance;
                if (inst != null)
                {
                    ExtractCurvesFromGeometry(inst.GetInstanceGeometry(), curves);
                    ExtractCurvesFromGeometry(inst.GetSymbolGeometry(), curves);
                    continue;
                }

                // Solid - extract edges
                Solid solid = obj as Solid;
                if (solid != null && solid.Edges != null)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        try
                        {
                            Curve edgeCurve = edge.AsCurve();
                            if (edgeCurve != null)
                                curves.Add(edgeCurve);
                        }
                        catch { }
                    }
                    continue;
                }

                // Nested geometry element
                GeometryElement nestedGeom = obj as GeometryElement;
                if (nestedGeom != null)
                {
                    ExtractCurvesFromGeometry(nestedGeom, curves);
                }
            }
        }

        public double GetPlotArea(CurveLoop plotBoundary)
        {
            if (plotBoundary == null) return 0.0;

            try
            {
                List<XYZ> vertices = new List<XYZ>();
                foreach (Curve curve in plotBoundary)
                {
                    vertices.Add(curve.GetEndPoint(0));
                }

                double area = 0.0;
                int n = vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    XYZ p1 = vertices[i];
                    XYZ p2 = vertices[(i + 1) % n];
                    area += (p1.X * p2.Y) - (p2.X * p1.Y);
                }

                double areaFeet = Math.Abs(area / 2.0);
                return areaFeet * 0.092903; // Convert to square meters
            }
            catch
            {
                return 0.0;
            }
        }

        private List<Curve> SortCurvesIntoContinuousLoop(List<Curve> curves)
        {
            if (curves == null || curves.Count < 3)
                return null;

            List<Curve> remaining = new List<Curve>(curves);
            List<Curve> sorted = new List<Curve>();

            sorted.Add(remaining[0]);
            remaining.RemoveAt(0);

            double tolerance = 0.1; // feet

            while (remaining.Count > 0)
            {
                XYZ lastEnd = sorted[sorted.Count - 1].GetEndPoint(1);
                bool found = false;

                for (int i = 0; i < remaining.Count; i++)
                {
                    Curve candidate = remaining[i];

                    // Check if start point connects
                    if (lastEnd.DistanceTo(candidate.GetEndPoint(0)) < tolerance)
                    {
                        sorted.Add(candidate);
                        remaining.RemoveAt(i);
                        found = true;
                        break;
                    }

                    // Check if end point connects (need to reverse)
                    if (lastEnd.DistanceTo(candidate.GetEndPoint(1)) < tolerance)
                    {
                        sorted.Add(candidate.CreateReversed());
                        remaining.RemoveAt(i);
                        found = true;
                        break;
                    }
                }

                if (!found) break;
            }

            // Verify the loop closes
            XYZ firstStart = sorted[0].GetEndPoint(0);
            XYZ lastEndPt = sorted[sorted.Count - 1].GetEndPoint(1);

            if (firstStart.DistanceTo(lastEndPt) > tolerance)
                return null;

            return sorted;
        }
    }
}