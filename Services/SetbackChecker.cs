using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Checks setback compliance for buildings
    /// </summary>
    public class SetbackChecker
    {
        private readonly Document _doc;
        private const double FEET_TO_METERS = 0.3048;
        private const double TOLERANCE = 0.01; // feet

        public SetbackChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>
        /// Checks all setbacks (front, rear, and both sides) with DCPR 2034 complete rules
        /// Includes: Road-width based + Height-based + Plot-size based + Building-type based
        /// </summary>
        public List<ComplianceResult> CheckSetbacks(DCRParameters dcrParams, CurveLoop plotBoundary)
        {
            var results = new List<ComplianceResult>();

            try
            {
                // Get all walls excluding basement walls
                FilteredElementCollector wallCollector = new FilteredElementCollector(_doc);
                var walls = wallCollector
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => !IsBasementWall(w, dcrParams.GroundLevelName))
                    .ToList();

                if (walls.Count == 0)
                {
                    results.Add(CreateWarningResult("All Setbacks", "No walls found above ground level"));
                    return results;
                }

                // Get plot boundary curves
                var boundaryCurves = plotBoundary.ToList();

                if (boundaryCurves.Count < 4)
                {
                    results.Add(CreateWarningResult("All Setbacks", "Plot boundary does not have 4 sides"));
                    return results;
                }

                // Calculate building height and plot area for additional rules
                double buildingHeightMeters = CalculateBuildingHeight(dcrParams.GroundLevelName);
                double plotAreaSqm = CalculatePlotArea(plotBoundary);
                
                // Determine if it's a large plot (> 1000 sqm)
                bool isLargePlot = plotAreaSqm > 1000.0;
                
                // Determine minimum setbacks based on building type (DCPR Regulation 41)
                double minSideRearResidential = 3.6;  // meters
                double minSideRearCommercial = 4.5;   // meters
                // Get the applicable minimum based on project type
                double minSideRear = (dcrParams.ProjectType == "Commercial" || dcrParams.ProjectType == "Mixed") 
                    ? minSideRearCommercial 
                    : minSideRearResidential;

                // Calculate height-based setback requirement (for buildings > 32m height)
                double heightBasedSetback = 0.0;
                if (buildingHeightMeters > 32.0)
                {
                    // DCPR 2034 Regulation 41(2): 
                    // - Small plots (≤1000 sqm): H/5 (max 12m)
                    // - Large plots (>1000 sqm): H/4 (max 12m)
                    double divisor = isLargePlot ? 4.0 : 5.0;
                    heightBasedSetback = Math.Min(buildingHeightMeters / divisor, 12.0);
                }

                // Identify boundaries by orientation
                var (north, south, east, west) = IdentifyBoundariesByOrientation(boundaryCurves);

                // Check each setback direction with enhanced rules
                // Internal road setbacks: if an internal road exists on a side,
                // its road-width-based setback applies alongside external road setback.
                // Governing rule = maximum of all applicable setbacks.
                
                // FRONT SETBACK: Road-width based (external + internal)
                double internalFrontSetback = MumbaiDCRData.GetSetbackForRoadWidth(dcrParams.InternalRoadWidthFront);
                double requiredFront = Math.Max(
                    Math.Max(dcrParams.FrontSetback, internalFrontSetback),
                    minSideRear);
                
                results.Add(CheckSetbackForDirectionEnhanced(
                    "Front Setback", 
                    requiredFront,
                    dcrParams.FrontSetback,
                    internalFrontSetback,    // Pass internal road setback via heightBasedSetback slot
                    minSideRear,
                    north, 
                    walls,
                    buildingHeightMeters,
                    plotAreaSqm,
                    isLargePlot,
                    dcrParams.InternalRoadWidthFront));

                // REAR SETBACK: Maximum of (external road-based, internal road-based, height-based, minimum)
                double internalRearSetback = MumbaiDCRData.GetSetbackForRoadWidth(dcrParams.InternalRoadWidthRear);
                double requiredRear = Math.Max(
                    Math.Max(dcrParams.RearSetback, internalRearSetback),
                    Math.Max(heightBasedSetback, minSideRear));
                
                results.Add(CheckSetbackForDirectionEnhanced(
                    "Rear Setback", 
                    requiredRear,
                    dcrParams.RearSetback,
                    heightBasedSetback,
                    minSideRear,
                    south, 
                    walls,
                    buildingHeightMeters,
                    plotAreaSqm,
                    isLargePlot,
                    dcrParams.InternalRoadWidthRear));

                // SIDE SETBACK 1 (East): Maximum of (external road, internal road, height-based, minimum)
                double internalSide1Setback = MumbaiDCRData.GetSetbackForRoadWidth(dcrParams.InternalRoadWidthSide1);
                double requiredSide1 = Math.Max(
                    Math.Max(dcrParams.SideSetback1, internalSide1Setback),
                    Math.Max(heightBasedSetback, minSideRear));
                
                results.Add(CheckSetbackForDirectionEnhanced(
                    "Side 1 Setback", 
                    requiredSide1,
                    dcrParams.SideSetback1,
                    heightBasedSetback,
                    minSideRear,
                    east, 
                    walls,
                    buildingHeightMeters,
                    plotAreaSqm,
                    isLargePlot,
                    dcrParams.InternalRoadWidthSide1));

                // SIDE SETBACK 2 (West): Maximum of (external road, internal road, height-based, minimum)
                double internalSide2Setback = MumbaiDCRData.GetSetbackForRoadWidth(dcrParams.InternalRoadWidthSide2);
                double requiredSide2 = Math.Max(
                    Math.Max(dcrParams.SideSetback2, internalSide2Setback),
                    Math.Max(heightBasedSetback, minSideRear));
                
                results.Add(CheckSetbackForDirectionEnhanced(
                    "Side 2 Setback", 
                    requiredSide2,
                    dcrParams.SideSetback2,
                    heightBasedSetback,
                    minSideRear,
                    west, 
                    walls,
                    buildingHeightMeters,
                    plotAreaSqm,
                    isLargePlot,
                    dcrParams.InternalRoadWidthSide2));
            }
            catch (Exception ex)
            {
                results.Add(CreateWarningResult("Setback Check", $"Error: {ex.Message}"));
            }

            return results;
        }

        /// <summary>
        /// Calculate building height from ground level to top
        /// </summary>
        private double CalculateBuildingHeight(string groundLevelName)
        {
            try
            {
                // Get ground level
                FilteredElementCollector levelCollector = new FilteredElementCollector(_doc);
                var levels = levelCollector.OfClass(typeof(Level))
                                          .Cast<Level>()
                                          .OrderBy(l => l.Elevation)
                                          .ToList();

                Level groundLevel = levels.FirstOrDefault(l => l.Name == groundLevelName);
                if (groundLevel == null)
                    groundLevel = levels.FirstOrDefault(); // Fallback to lowest level

                if (groundLevel == null)
                    return 0.0;

                // Get highest level
                Level topLevel = levels.LastOrDefault();
                if (topLevel == null)
                    return 0.0;

                // Calculate height in feet, then convert to meters
                double heightFeet = topLevel.Elevation - groundLevel.Elevation;
                return heightFeet * FEET_TO_METERS;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Calculate plot area from boundary
        /// </summary>
        private double CalculatePlotArea(CurveLoop plotBoundary)
        {
            try
            {
                List<XYZ> vertices = new List<XYZ>();
                foreach (Curve curve in plotBoundary)
                {
                    vertices.Add(curve.GetEndPoint(0));
                }

                // Shoelace formula for area
                double area = 0.0;
                int n = vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    XYZ p1 = vertices[i];
                    XYZ p2 = vertices[(i + 1) % n];
                    area += (p1.X * p2.Y) - (p2.X * p1.Y);
                }

                double areaFeet = Math.Abs(area / 2.0);
                return areaFeet * 0.092903; // Convert to sqm
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Enhanced setback check with detailed breakdown of rules
        /// </summary>
        private ComplianceResult CheckSetbackForDirectionEnhanced(
            string parameterName,
            double requiredSetbackMeters,
            double roadBasedSetback,
            double heightBasedSetback,
            double minimumSetback,
            Curve boundaryCurve,
            List<Wall> walls,
            double buildingHeightMeters,
            double plotAreaSqm,
            bool isLargePlot,
            double internalRoadWidth = 0.0)
        {
            try
            {
                // Find the wall closest to this boundary
                double minDistance = double.MaxValue;
                Wall closestWall = null;
                XYZ closestPoint = null;

                XYZ boundaryMidpoint = boundaryCurve.Evaluate(0.5, true);

                foreach (var wall in walls)
                {
                    LocationCurve locCurve = wall.Location as LocationCurve;
                    if (locCurve == null) continue;

                    XYZ wallMidpoint = locCurve.Curve.Evaluate(0.5, true);
                    double distance = wallMidpoint.DistanceTo(boundaryMidpoint);

                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closestWall = wall;
                        closestPoint = wallMidpoint;
                    }
                }

                if (closestWall == null)
                {
                    return CreateWarningResult(parameterName, "No walls found near this boundary");
                }

                // Calculate perpendicular distance from wall centerline to boundary
                double centerlineDistFeet = CalculatePerpendicularDistance(closestPoint, boundaryCurve);

                // Subtract half wall width to get finish face distance
                double halfWidthFeet = closestWall.Width / 2.0;
                double actualDistanceFeet = centerlineDistFeet - halfWidthFeet;
                if (actualDistanceFeet < 0) actualDistanceFeet = 0;

                double actualDistanceMeters = actualDistanceFeet * FEET_TO_METERS;

                // Calculate internal road setback if applicable
                double internalRoadSetback = MumbaiDCRData.GetSetbackForRoadWidth(internalRoadWidth);

                // Determine which rule governs
                string governingRule = "";
                if (requiredSetbackMeters <= roadBasedSetback + 0.01 && roadBasedSetback > 0)
                    governingRule = "External Road Width";
                else if (internalRoadWidth > 0 && requiredSetbackMeters <= internalRoadSetback + 0.01)
                    governingRule = "Internal Road Width";
                else if (requiredSetbackMeters <= heightBasedSetback + 0.01 && heightBasedSetback > 0)
                    governingRule = buildingHeightMeters > 32.0 
                        ? (isLargePlot ? "Building Height (H/4)" : "Building Height (H/5)")
                        : "Minimum";
                else
                    governingRule = "Minimum Required";

                // Build detailed explanation
                string ruleBreakdown = "\nApplied Rules:";
                ruleBreakdown += string.Format("\n• Ext. road-based: {0:F2}m", roadBasedSetback);
                if (internalRoadWidth > 0)
                    ruleBreakdown += string.Format("\n• Int. road-based ({0:F1}m road): {1:F2}m", 
                        internalRoadWidth, internalRoadSetback);
                if (buildingHeightMeters > 32.0)
                    ruleBreakdown += string.Format("\n• Height-based ({0}): {1:F2}m",
                        isLargePlot ? "H/4" : "H/5", heightBasedSetback);
                ruleBreakdown += string.Format("\n• Minimum: {0:F2}m", minimumSetback);
                ruleBreakdown += string.Format("\n• Governing: {0}", governingRule);

                bool passed = actualDistanceMeters >= requiredSetbackMeters - 0.01;

                return new ComplianceResult
                {
                    Parameter = parameterName,
                    Required = $"≥ {requiredSetbackMeters:F2} m",
                    Actual = $"{actualDistanceMeters:F2} m",
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed
                        ? $"Compliant{ruleBreakdown}"
                        : $"Violation: {(requiredSetbackMeters - actualDistanceMeters):F2}m short{ruleBreakdown}",
                    ViolatingElementIds = passed ? null : new[] { closestWall.Id }
                };
            }
            catch (Exception ex)
            {
                return CreateWarningResult(parameterName, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a wall is a basement wall
        /// </summary>
        private bool IsBasementWall(Wall wall, string groundLevelName)
        {
            try
            {
                Level baseLevel = _doc.GetElement(wall.LevelId) as Level;
                if (baseLevel == null)
                    return false;

                // Check if level name contains "basement" or "parking"
                string levelName = baseLevel.Name.ToLower();
                if (levelName.Contains("basement") || levelName.Contains("parking"))
                    return true;

                // If ground level is specified, check if wall is below it
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

        /// <summary>
        /// Identifies plot boundaries by orientation (North, South, East, West)
        /// </summary>
        private (Curve north, Curve south, Curve east, Curve west) IdentifyBoundariesByOrientation(List<Curve> curves)
        {
            // Sort by Y-coordinate for North/South
            var sortedByY = curves.OrderByDescending(c => GetCurveMidpoint(c).Y).ToList();
            Curve north = sortedByY[0];
            Curve south = sortedByY[sortedByY.Count - 1];

            // Sort by X-coordinate for East/West
            var sortedByX = curves.OrderByDescending(c => GetCurveMidpoint(c).X).ToList();
            Curve east = sortedByX[0];
            Curve west = sortedByX[sortedByX.Count - 1];

            return (north, south, east, west);
        }

        /// <summary>
        /// Gets the midpoint of a curve
        /// </summary>
        private XYZ GetCurveMidpoint(Curve curve)
        {
            return (curve.GetEndPoint(0) + curve.GetEndPoint(1)) / 2.0;
        }

        /// <summary>
        /// Checks setback compliance for a specific direction
        /// </summary>
        private ComplianceResult CheckSetbackForDirection(string direction, double requiredSetback, Curve boundaryCurve, List<Wall> walls)
        {
            try
            {
                double minDistance = double.MaxValue;
                List<ElementId> violatingWalls = new List<ElementId>();

                foreach (var wall in walls)
                {
                    LocationCurve wallLocation = wall.Location as LocationCurve;
                    if (wallLocation != null)
                    {
                        Curve wallCurve = wallLocation.Curve;
                        double centerlineDist = GetMinimumDistanceBetweenCurves(wallCurve, boundaryCurve);

                        // Subtract half wall width to get finish face distance
                        double halfWidth = wall.Width / 2.0;
                        double faceDist = centerlineDist - halfWidth;
                        if (faceDist < 0) faceDist = 0;
                        
                        if (faceDist < minDistance)
                            minDistance = faceDist;

                        // Check if this wall violates setback (from finish face)
                        double distanceMeters = faceDist * FEET_TO_METERS;
                        if (distanceMeters < requiredSetback - 0.01) // Small tolerance for floating point
                            violatingWalls.Add(wall.Id);
                    }
                }

                // Convert minimum distance from feet to meters
                double actualSetbackMeters = minDistance * FEET_TO_METERS;
                bool passed = actualSetbackMeters >= requiredSetback - 0.01;

                return new ComplianceResult
                {
                    Parameter = direction,
                    Required = $"{requiredSetback:F2} m",
                    Actual = $"{actualSetbackMeters:F2} m",
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed 
                        ? "Compliant" 
                        : $"{violatingWalls.Count} wall(s) violate setback by {(requiredSetback - actualSetbackMeters):F2} m",
                    ViolatingElementIds = violatingWalls.ToArray()
                };
            }
            catch (Exception ex)
            {
                return CreateWarningResult(direction, $"Calculation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates minimum distance between two curves
        /// </summary>
        private double GetMinimumDistanceBetweenCurves(Curve curve1, Curve curve2)
        {
            double minDistance = double.MaxValue;
            
            // Sample multiple points along each curve for more accurate distance
            int samples = 10;
            
            for (int i = 0; i <= samples; i++)
            {
                double param1 = i / (double)samples;
                XYZ point1 = curve1.Evaluate(param1, true);
                
                for (int j = 0; j <= samples; j++)
                {
                    double param2 = j / (double)samples;
                    XYZ point2 = curve2.Evaluate(param2, true);
                    
                    double distance = point1.DistanceTo(point2);
                    if (distance < minDistance)
                        minDistance = distance;
                }
            }
            
            return minDistance;
        }

        /// <summary>
        /// Gets a level by name
        /// </summary>
        private Level GetLevelByName(string levelName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(_doc);
            return collector
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName);
        }

        /// <summary>
        /// Calculates the perpendicular distance from a point to a curve, in feet
        /// </summary>
        private double CalculatePerpendicularDistance(XYZ point, Curve curve)
        {
            try
            {
                IntersectionResult result = curve.Project(point);
                if (result != null)
                    return result.Distance;

                // Fallback: minimum distance to curve endpoints
                double d0 = point.DistanceTo(curve.GetEndPoint(0));
                double d1 = point.DistanceTo(curve.GetEndPoint(1));
                return Math.Min(d0, d1);
            }
            catch
            {
                return point.DistanceTo(curve.Evaluate(0.5, true));
            }
        }

        /// <summary>
        /// Creates a warning result
        /// </summary>
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
    }
}
