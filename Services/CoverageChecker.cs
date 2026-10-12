using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    public class CoverageChecker
    {
        private readonly Document _doc;
        private const double SQFT_TO_SQM = 0.092903;

        public CoverageChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public ComplianceResult CheckGroundCoverage(DCRParameters dcrParams, CurveLoop plotBoundary)
        {
            try
            {
                double plotArea = GetCurveLoopArea(plotBoundary);
                double plotAreaSqm = plotArea * SQFT_TO_SQM;

                var groundLevel = GetLevelByName(dcrParams.GroundLevelName);
                if (groundLevel == null)
                {
                    return CreateWarningResult("Ground Coverage", "Ground level not found");
                }

                double footprintArea = CalculateCombinedFootprint(groundLevel);
                double footprintSqm = footprintArea * SQFT_TO_SQM;
                double coveragePercent = (footprintSqm / plotAreaSqm) * 100;
                bool passed = coveragePercent <= dcrParams.GroundCoveragePercent + 0.1;

                return new ComplianceResult
                {
                    Parameter = "Ground Coverage",
                    Required = $"≤ {dcrParams.GroundCoveragePercent:F1}%",
                    Actual = $"{coveragePercent:F1}%",
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed
                        ? $"Compliant ({footprintSqm:F2} sqm / {plotAreaSqm:F2} sqm)"
                        : $"Exceeds by {(coveragePercent - dcrParams.GroundCoveragePercent):F1}%"
                };
            }
            catch (Exception ex)
            {
                return CreateWarningResult("Ground Coverage", $"Error: {ex.Message}");
            }
        }

        public ComplianceResult CheckOpenSpace(DCRParameters dcrParams, CurveLoop plotBoundary)
        {
            try
            {
                double plotArea = GetCurveLoopArea(plotBoundary);
                double plotAreaSqm = plotArea * SQFT_TO_SQM;

                var groundLevel = GetLevelByName(dcrParams.GroundLevelName);
                if (groundLevel == null)
                {
                    return CreateWarningResult("Open Space", "Ground level not found");
                }

                double footprintArea = CalculateCombinedFootprint(groundLevel);
                double footprintSqm = footprintArea * SQFT_TO_SQM;
                double openSpaceSqm = plotAreaSqm - footprintSqm;
                double openSpacePercent = (openSpaceSqm / plotAreaSqm) * 100;
                bool passed = openSpacePercent >= dcrParams.OpenSpacePercent - 0.1;

                return new ComplianceResult
                {
                    Parameter = "Open Space",
                    Required = $"≥ {dcrParams.OpenSpacePercent:F1}%",
                    Actual = $"{openSpacePercent:F1}%",
                    Status = passed ? "PASS" : "WARNING",
                    Details = passed
                        ? $"Compliant ({openSpaceSqm:F2} sqm)"
                        : $"Deficient by {(dcrParams.OpenSpacePercent - openSpacePercent):F1}% - verify manually"
                };
            }
            catch (Exception ex)
            {
                return CreateWarningResult("Open Space", $"Error: {ex.Message}");
            }
        }

        private double CalculateCombinedFootprint(Level groundLevel)
        {
            FilteredElementCollector wallCollector = new FilteredElementCollector(_doc);
            var walls = wallCollector.OfClass(typeof(Wall))
                                    .WhereElementIsNotElementType()
                                    .Cast<Wall>()
                                    .Where(w => IsWallAtLevel(w, groundLevel))
                                    .ToList();

            if (walls.Count == 0) return 0;

            BoundingBoxXYZ bbox = null;
            foreach (var wall in walls)
            {
                BoundingBoxXYZ wallBox = wall.get_BoundingBox(null);
                if (wallBox != null)
                {
                    if (bbox == null)
                    {
                        bbox = new BoundingBoxXYZ();
                        bbox.Min = wallBox.Min;
                        bbox.Max = wallBox.Max;
                    }
                    else
                    {
                        bbox.Min = new XYZ(
                            Math.Min(bbox.Min.X, wallBox.Min.X),
                            Math.Min(bbox.Min.Y, wallBox.Min.Y),
                            bbox.Min.Z);
                        bbox.Max = new XYZ(
                            Math.Max(bbox.Max.X, wallBox.Max.X),
                            Math.Max(bbox.Max.Y, wallBox.Max.Y),
                            bbox.Max.Z);
                    }
                }
            }

            if (bbox == null) return 0;

            double length = bbox.Max.X - bbox.Min.X;
            double width = bbox.Max.Y - bbox.Min.Y;
            return length * width;
        }

        private bool IsWallAtLevel(Wall wall, Level level)
        {
            Level wallLevel = _doc.GetElement(wall.LevelId) as Level;
            return wallLevel != null && wallLevel.Id == level.Id;
        }

        private Level GetLevelByName(string levelName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(_doc);
            return collector.OfClass(typeof(Level))
                           .Cast<Level>()
                           .FirstOrDefault(l => l.Name == levelName);
        }

        /// <summary>
        /// Calculates the area of a CurveLoop in square feet
        /// </summary>
        private double GetCurveLoopArea(CurveLoop curveLoop)
        {
            try
            {
                // Get the plane of the curve loop
                Plane plane = curveLoop.GetPlane();

                // Create a list to store the vertices
                List<XYZ> vertices = new List<XYZ>();
                foreach (Curve curve in curveLoop)
                {
                    vertices.Add(curve.GetEndPoint(0));
                }

                // Calculate area using the shoelace formula (2D projection on the plane)
                double area = 0.0;
                int n = vertices.Count;

                for (int i = 0; i < n; i++)
                {
                    XYZ p1 = vertices[i];
                    XYZ p2 = vertices[(i + 1) % n];

                    // Project points onto the plane's coordinate system
                    UV uv1 = new UV(p1.X, p1.Y);
                    UV uv2 = new UV(p2.X, p2.Y);

                    area += (uv1.U * uv2.V) - (uv2.U * uv1.V);
                }

                return Math.Abs(area / 2.0);
            }
            catch
            {
                // Fallback: approximate using bounding box
                return GetApproximateAreaFromBoundingBox(curveLoop);
            }
        }

        /// <summary>
        /// Fallback method to estimate area from bounding box
        /// </summary>
        private double GetApproximateAreaFromBoundingBox(CurveLoop curveLoop)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (Curve curve in curveLoop)
            {
                XYZ start = curve.GetEndPoint(0);
                XYZ end = curve.GetEndPoint(1);

                minX = Math.Min(minX, Math.Min(start.X, end.X));
                minY = Math.Min(minY, Math.Min(start.Y, end.Y));
                maxX = Math.Max(maxX, Math.Max(start.X, end.X));
                maxY = Math.Max(maxY, Math.Max(start.Y, end.Y));
            }

            return (maxX - minX) * (maxY - minY);
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
    }
}