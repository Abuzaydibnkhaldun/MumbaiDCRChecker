using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Phase 2 Feature: Checks setback compliance using Reference Planes.
    /// User creates Reference Planes named "Front Setback", "Rear Setback", etc.
    /// Plugin auto-detects and measures wall distances to these planes.
    /// Falls back to automatic boundary detection if no Reference Planes found.
    /// </summary>
    public class RefPlaneSetbackChecker
    {
        private readonly Document _doc;
        private const double FEET_TO_METERS = 0.3048;
        private const double TOLERANCE = 0.01;

        public RefPlaneSetbackChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>
        /// Attempts to find setback Reference Planes in the model.
        /// Returns null if none found (caller should fall back to automatic method).
        /// </summary>
        public List<ComplianceResult> TryCheckWithRefPlanes(DCRParameters dcrParams)
        {
            try
            {
                // Step 1: Find all Reference Planes
                var allRefPlanes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ReferencePlane))
                    .Cast<ReferencePlane>()
                    .ToList();

                if (allRefPlanes.Count == 0)
                    return null; // No ref planes at all - fall back

                // Step 2: Match planes to setback directions
                ReferencePlane frontPlane = null;
                ReferencePlane rearPlane = null;
                ReferencePlane side1Plane = null;
                ReferencePlane side2Plane = null;

                foreach (var plane in allRefPlanes)
                {
                    string name = (plane.Name ?? "").ToLower().Trim();
                    if (string.IsNullOrEmpty(name)) continue;

                    if (IsFrontSetback(name))
                        frontPlane = plane;
                    else if (IsRearSetback(name))
                        rearPlane = plane;
                    else if (IsSide1Setback(name))
                        side1Plane = plane;
                    else if (IsSide2Setback(name))
                        side2Plane = plane;
                }

                // Step 3: Need at least one setback plane to proceed
                int foundCount = (frontPlane != null ? 1 : 0)
                               + (rearPlane != null ? 1 : 0)
                               + (side1Plane != null ? 1 : 0)
                               + (side2Plane != null ? 1 : 0);

                if (foundCount == 0)
                    return null; // No setback planes found - fall back

                // Step 4: Get walls above ground level
                var walls = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => !IsBasementWall(w, dcrParams.GroundLevelName))
                    .ToList();

                if (walls.Count == 0)
                {
                    return new List<ComplianceResult>
                    {
                        CreateWarningResult("Setbacks (Ref Planes)", "No walls found above ground level")
                    };
                }

                // Step 5: Calculate enhanced setback requirements
                double buildingHeightMeters = CalculateBuildingHeight(dcrParams.GroundLevelName);
                double plotAreaSqm = CalculatePlotAreaFromWalls(walls);
                bool isLargePlot = plotAreaSqm > 1000.0;

                double minSideRear = (dcrParams.ProjectType == "Commercial" || dcrParams.ProjectType == "Mixed")
                    ? 4.5 : 3.6;

                double heightBasedSetback = 0.0;
                if (buildingHeightMeters > 32.0)
                {
                    double divisor = isLargePlot ? 4.0 : 5.0;
                    heightBasedSetback = Math.Min(buildingHeightMeters / divisor, 12.0);
                }

                // Step 6: Check each direction
                var results = new List<ComplianceResult>();
                string methodNote = string.Format(" [Ref Planes: {0}/4 found]", foundCount);

                if (frontPlane != null)
                {
                    double required = dcrParams.FrontSetback; // Front: road-based only
                    results.Add(CheckDistanceToPlane(
                        "Front Setback" + methodNote, required,
                        dcrParams.FrontSetback, 0.0, minSideRear,
                        frontPlane, walls, buildingHeightMeters, isLargePlot, true));
                }
                else
                {
                    results.Add(CreateInfoResult("Front Setback",
                        "No 'Front Setback' Reference Plane found - skipped"));
                }

                if (rearPlane != null)
                {
                    double required = Math.Max(Math.Max(dcrParams.RearSetback, heightBasedSetback), minSideRear);
                    results.Add(CheckDistanceToPlane(
                        "Rear Setback" + methodNote, required,
                        dcrParams.RearSetback, heightBasedSetback, minSideRear,
                        rearPlane, walls, buildingHeightMeters, isLargePlot, false));
                }
                else
                {
                    results.Add(CreateInfoResult("Rear Setback",
                        "No 'Rear Setback' Reference Plane found - skipped"));
                }

                if (side1Plane != null)
                {
                    double required = Math.Max(Math.Max(dcrParams.SideSetback1, heightBasedSetback), minSideRear);
                    results.Add(CheckDistanceToPlane(
                        "Side Setback 1" + methodNote, required,
                        dcrParams.SideSetback1, heightBasedSetback, minSideRear,
                        side1Plane, walls, buildingHeightMeters, isLargePlot, false));
                }
                else
                {
                    results.Add(CreateInfoResult("Side Setback 1",
                        "No 'Side Setback 1' Reference Plane found - skipped"));
                }

                if (side2Plane != null)
                {
                    double required = Math.Max(Math.Max(dcrParams.SideSetback2, heightBasedSetback), minSideRear);
                    results.Add(CheckDistanceToPlane(
                        "Side Setback 2" + methodNote, required,
                        dcrParams.SideSetback2, heightBasedSetback, minSideRear,
                        side2Plane, walls, buildingHeightMeters, isLargePlot, false));
                }
                else
                {
                    results.Add(CreateInfoResult("Side Setback 2",
                        "No 'Side Setback 2' Reference Plane found - skipped"));
                }

                return results;
            }
            catch (Exception ex)
            {
                // On any error, return null to fall back to automatic method
                System.Diagnostics.Debug.WriteLine("RefPlaneSetbackChecker error: " + ex.Message);
                return null;
            }
        }

        // ============================================================
        // Name matching - case-insensitive keyword detection
        // ============================================================

        private bool IsFrontSetback(string name)
        {
            bool hasSetback = name.Contains("setback") || name.Contains("set back");
            bool hasFront = name.Contains("front") || name.Contains("north");
            return hasSetback && hasFront;
        }

        private bool IsRearSetback(string name)
        {
            bool hasSetback = name.Contains("setback") || name.Contains("set back");
            bool hasRear = name.Contains("rear") || name.Contains("south") || name.Contains("back");
            // Avoid matching "setback" itself as "back"
            if (hasRear && name.Contains("setback") && !name.Contains("rear") && !name.Contains("south"))
            {
                // Only "back" matched, but it's part of "setback" - check if "back" appears separately
                hasRear = name.Contains("rear") || name.Contains("south");
            }
            return hasSetback && hasRear;
        }

        private bool IsSide1Setback(string name)
        {
            bool hasSetback = name.Contains("setback") || name.Contains("set back");
            bool hasSide1 = (name.Contains("side") && name.Contains("1"))
                         || name.Contains("east")
                         || name.Contains("left");
            return hasSetback && hasSide1;
        }

        private bool IsSide2Setback(string name)
        {
            bool hasSetback = name.Contains("setback") || name.Contains("set back");
            bool hasSide2 = (name.Contains("side") && name.Contains("2"))
                         || name.Contains("west")
                         || name.Contains("right");
            return hasSetback && hasSide2;
        }

        // ============================================================
        // Distance measurement - wall to Reference Plane
        // ============================================================

        private ComplianceResult CheckDistanceToPlane(
            string parameterName,
            double requiredMeters,
            double roadBased,
            double heightBased,
            double minimum,
            ReferencePlane refPlane,
            List<Wall> walls,
            double buildingHeightMeters,
            bool isLargePlot,
            bool isFront)
        {
            try
            {
                // Get the plane's geometry for distance calculation
                XYZ planeOrigin = refPlane.GetPlane().Origin;
                XYZ planeNormal = refPlane.GetPlane().Normal;

                double minDistanceFeet = double.MaxValue;
                Wall closestWall = null;

                foreach (var wall in walls)
                {
                    LocationCurve locCurve = wall.Location as LocationCurve;
                    if (locCurve == null) continue;

                    Curve wallCurve = locCurve.Curve;

                    // Get half wall thickness to measure from finish face
                    double halfWidth = wall.Width / 2.0;

                    // Sample multiple points along the wall for accuracy
                    for (int i = 0; i <= 10; i++)
                    {
                        double param = i / 10.0;
                        XYZ point = wallCurve.Evaluate(param, true);

                        // Perpendicular distance from centerline point to plane
                        XYZ toPoint = point - planeOrigin;
                        double centerlineDist = Math.Abs(toPoint.DotProduct(planeNormal));

                        // Subtract half wall width to get finish face distance
                        double faceDist = centerlineDist - halfWidth;
                        if (faceDist < 0) faceDist = 0;

                        if (faceDist < minDistanceFeet)
                        {
                            minDistanceFeet = faceDist;
                            closestWall = wall;
                        }
                    }
                }

                if (closestWall == null)
                {
                    return CreateWarningResult(parameterName, "No walls found near this Reference Plane");
                }

                double actualMeters = minDistanceFeet * FEET_TO_METERS;
                bool passed = actualMeters >= requiredMeters - 0.01;

                // Build rule breakdown
                string ruleBreakdown = BuildRuleBreakdown(
                    roadBased, heightBased, minimum,
                    buildingHeightMeters, isLargePlot, isFront, requiredMeters);

                return new ComplianceResult
                {
                    Parameter = parameterName,
                    Required = string.Format(">= {0:F2} m", requiredMeters),
                    Actual = string.Format("{0:F2} m", actualMeters),
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed
                        ? string.Format("Compliant{0}", ruleBreakdown)
                        : string.Format("Violation: {0:F2}m short{1}",
                            requiredMeters - actualMeters, ruleBreakdown),
                    ViolatingElementIds = passed ? null : new[] { closestWall.Id }
                };
            }
            catch (Exception ex)
            {
                return CreateWarningResult(parameterName, "Error: " + ex.Message);
            }
        }

        private string BuildRuleBreakdown(
            double roadBased, double heightBased, double minimum,
            double buildingHeightMeters, bool isLargePlot, bool isFront,
            double required)
        {
            if (isFront)
            {
                return string.Format("\n[Road-based: {0:F2}m]", roadBased);
            }

            // Determine governing rule
            string governing = "Road Width";
            if (required == heightBased && heightBased > 0)
                governing = isLargePlot ? "Height (H/4)" : "Height (H/5)";
            else if (required == minimum)
                governing = "Minimum";

            if (buildingHeightMeters > 32.0)
            {
                return string.Format(
                    "\n[Road: {0:F2}m | Height({1}): {2:F2}m | Min: {3:F2}m | Governing: {4}]",
                    roadBased,
                    isLargePlot ? "H/4" : "H/5",
                    heightBased,
                    minimum,
                    governing);
            }

            return string.Format(
                "\n[Road: {0:F2}m | Min: {1:F2}m | Governing: {2}]",
                roadBased, minimum, governing);
        }

        // ============================================================
        // Helper methods
        // ============================================================

        private bool IsBasementWall(Wall wall, string groundLevelName)
        {
            try
            {
                Level baseLevel = _doc.GetElement(wall.LevelId) as Level;
                if (baseLevel == null) return false;

                string levelName = baseLevel.Name.ToLower();
                if (levelName.Contains("basement") || levelName.Contains("parking"))
                    return true;

                if (!string.IsNullOrEmpty(groundLevelName))
                {
                    Level groundLevel = GetLevelByName(groundLevelName);
                    if (groundLevel != null && baseLevel.Elevation < groundLevel.Elevation - TOLERANCE)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private double CalculateBuildingHeight(string groundLevelName)
        {
            try
            {
                var levels = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                Level groundLevel = levels.FirstOrDefault(l => l.Name == groundLevelName);
                if (groundLevel == null) groundLevel = levels.FirstOrDefault();
                if (groundLevel == null) return 0.0;

                Level topLevel = levels.LastOrDefault();
                if (topLevel == null) return 0.0;

                return (topLevel.Elevation - groundLevel.Elevation) * FEET_TO_METERS;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Rough plot area estimate from wall bounding box (used for height-based rule threshold)
        /// </summary>
        private double CalculatePlotAreaFromWalls(List<Wall> walls)
        {
            try
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var wall in walls)
                {
                    LocationCurve loc = wall.Location as LocationCurve;
                    if (loc == null) continue;

                    XYZ s = loc.Curve.GetEndPoint(0);
                    XYZ e = loc.Curve.GetEndPoint(1);

                    minX = Math.Min(minX, Math.Min(s.X, e.X));
                    minY = Math.Min(minY, Math.Min(s.Y, e.Y));
                    maxX = Math.Max(maxX, Math.Max(s.X, e.X));
                    maxY = Math.Max(maxY, Math.Max(s.Y, e.Y));
                }

                double areaFt = (maxX - minX) * (maxY - minY);
                return areaFt * 0.092903; // sq ft to sq m
            }
            catch
            {
                return 0.0;
            }
        }

        private Level GetLevelByName(string levelName)
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName);
        }

        private ComplianceResult CreateWarningResult(string parameter, string details)
        {
            return new ComplianceResult
            {
                Parameter = parameter,
                Required = "N/A",
                Actual = "N/A",
                Status = "WARNING",
                Details = details
            };
        }

        private ComplianceResult CreateInfoResult(string parameter, string details)
        {
            return new ComplianceResult
            {
                Parameter = parameter,
                Required = "N/A",
                Actual = "N/A",
                Status = "WARNING",
                Details = details
            };
        }
    }
}
