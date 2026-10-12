using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    public class InterBuildingChecker
    {
        private readonly Document _doc;
        private const double FEET_TO_METERS = 0.3048;
        private const double CLUSTERING_DISTANCE = 3.28; // feet (~1.0m) — walls within same building join at corners (within mm)
        // 1m threshold chains connected walls while keeping separate buildings apart (even if only ~2m gap)

        public InterBuildingChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public ComplianceResult CheckInterBuildingDistance(DCRParameters dcrParams)
        {
            try
            {
                FilteredElementCollector wallCollector = new FilteredElementCollector(_doc);
                var walls = wallCollector.OfClass(typeof(Wall))
                                        .WhereElementIsNotElementType()
                                        .Cast<Wall>()
                                        .Where(w => IsAboveGround(w)) // Only above-ground walls
                                        .ToList();

                if (walls.Count < 2)
                {
                    return new ComplianceResult
                    {
                        Parameter = "Inter-Building Distance",
                        Required = "N/A",
                        Actual = "N/A",
                        Status = "PASS",
                        Details = "Not applicable - single building on plot"
                    };
                }

                var buildings = GroupWallsIntoBuildings(walls);

                if (buildings.Count < 2)
                {
                    return new ComplianceResult
                    {
                        Parameter = "Inter-Building Distance",
                        Required = "N/A",
                        Actual = "N/A",
                        Status = "PASS",
                        Details = "Not applicable - single building on plot"
                    };
                }

                double minDistance = CalculateMinDistanceBetweenBuildings(buildings);
                double minDistanceMeters = minDistance * FEET_TO_METERS;
                bool passed = minDistanceMeters >= dcrParams.MinInterBuildingDistance - 0.01;

                return new ComplianceResult
                {
                    Parameter = "Inter-Building Distance",
                    Required = $"≥ {dcrParams.MinInterBuildingDistance:F2} m",
                    Actual = $"{minDistanceMeters:F2} m",
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed 
                        ? $"Compliant — finish face to finish face ({buildings.Count} buildings detected)" 
                        : $"Insufficient by {(dcrParams.MinInterBuildingDistance - minDistanceMeters):F2} m (finish face to finish face)"
                };
            }
            catch (Exception ex)
            {
                return new ComplianceResult
                {
                    Parameter = "Inter-Building Distance",
                    Required = $"≥ {dcrParams.MinInterBuildingDistance:F2} m",
                    Actual = "Error",
                    Status = "WARNING",
                    Details = $"Calculation error: {ex.Message}"
                };
            }
        }

        private List<List<Wall>> GroupWallsIntoBuildings(List<Wall> walls)
        {
            var buildings = new List<List<Wall>>();
            var processed = new HashSet<ElementId>();

            // Use fixed clustering distance — do NOT scale to building span
            // (scaling caused all walls to merge into one group)
            double clusterDist = CLUSTERING_DISTANCE;

            foreach (var wall in walls)
            {
                if (processed.Contains(wall.Id))
                    continue;

                var building = new List<Wall>();
                var queue = new Queue<Wall>();
                queue.Enqueue(wall);
                processed.Add(wall.Id);

                while (queue.Count > 0)
                {
                    var currentWall = queue.Dequeue();
                    building.Add(currentWall);

                    foreach (var otherWall in walls)
                    {
                        if (processed.Contains(otherWall.Id))
                            continue;

                        if (AreWallsClose(currentWall, otherWall, clusterDist))
                        {
                            queue.Enqueue(otherWall);
                            processed.Add(otherWall.Id);
                        }
                    }
                }

                if (building.Count > 0)
                    buildings.Add(building);
            }

            return buildings;
        }

        /// <summary>
        /// Check if two walls are part of the same building by testing XY bounding box proximity.
        /// Uses bounding boxes (which capture the full wall extent) rather than endpoint sampling.
        /// Two walls whose XY envelopes overlap or are within maxDistance are considered same-building.
        /// </summary>
        private bool AreWallsClose(Wall wall1, Wall wall2, double maxDistance)
        {
            BoundingBoxXYZ bb1 = wall1.get_BoundingBox(null);
            BoundingBoxXYZ bb2 = wall2.get_BoundingBox(null);

            if (bb1 == null || bb2 == null)
                return false;

            // Check XY bounding box proximity (ignore Z — same building spans multiple floors)
            bool xNear = bb1.Min.X - maxDistance <= bb2.Max.X &&
                         bb2.Min.X - maxDistance <= bb1.Max.X;
            bool yNear = bb1.Min.Y - maxDistance <= bb2.Max.Y &&
                         bb2.Min.Y - maxDistance <= bb1.Max.Y;

            return xNear && yNear;
        }

        /// <summary>
        /// Check if a wall is above ground (not in basement)
        /// </summary>
        private bool IsAboveGround(Wall wall)
        {
            try
            {
                var bbox = wall.get_BoundingBox(null);
                if (bbox == null) return true; // assume above ground if no BB
                // If top of wall is below -0.5m (below grade), skip it
                if (bbox.Max.Z * FEET_TO_METERS < -0.5) return false;
                return true;
            }
            catch { return true; }
        }

        private double CalculateMinDistanceBetweenBuildings(List<List<Wall>> buildings)
        {
            double minDistance = double.MaxValue;

            for (int i = 0; i < buildings.Count - 1; i++)
            {
                for (int j = i + 1; j < buildings.Count; j++)
                {
                    double distance = CalculateDistanceBetweenBuildings(buildings[i], buildings[j]);
                    if (distance < minDistance)
                        minDistance = distance;
                }
            }

            return minDistance;
        }

        private double CalculateDistanceBetweenBuildings(List<Wall> building1, List<Wall> building2)
        {
            double minDistance = double.MaxValue;

            foreach (var wall1 in building1)
            {
                LocationCurve loc1 = wall1.Location as LocationCurve;
                if (loc1 == null) continue;
                double halfWidth1 = wall1.Width / 2.0; // half wall thickness in feet

                foreach (var wall2 in building2)
                {
                    LocationCurve loc2 = wall2.Location as LocationCurve;
                    if (loc2 == null) continue;
                    double halfWidth2 = wall2.Width / 2.0;

                    // Get centerline distance between curves
                    double centerlineDist = GetMinimumDistanceBetweenCurves(loc1.Curve, loc2.Curve);
                    
                    // Subtract both half-widths to get finish-face to finish-face
                    double faceDist = centerlineDist - halfWidth1 - halfWidth2;
                    if (faceDist < 0) faceDist = 0;

                    if (faceDist < minDistance)
                        minDistance = faceDist;
                }
            }

            return minDistance;
        }

        private double GetMinimumDistanceBetweenCurves(Curve curve1, Curve curve2)
        {
            double minDistance = double.MaxValue;
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
    }
}
