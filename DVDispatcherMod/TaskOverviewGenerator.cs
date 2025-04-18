﻿using DV.Logic.Job;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DVDispatcherMod
{
    public class TaskOverviewGenerator
    {
        private static readonly FieldInfo StationControllerStationRangeField = typeof(StationController).GetField("stationRange", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Dictionary<string, Color> _yardID2Color;

        public TaskOverviewGenerator()
        {
            _yardID2Color = StationController.allStations.ToDictionary(s => s.stationInfo.YardID, s => s.stationInfo.StationColor);
        }

        public void RecursivelyGetAllTasksData(Task task, ref int i, ref List<TaskData> JobTasks, ref List<int> indentList, int currentLevel = 0)
        {
            var TaskData = task.GetTaskData();
            if (!JobTasks.Contains(TaskData))
            {
                JobTasks.Add(TaskData);
                indentList.Add(currentLevel);
                i++;
            }
            if (TaskData.nestedTasks != null && TaskData.nestedTasks.Any())
            {
                foreach (Task NestedJobTask in TaskData.nestedTasks)
                {
                    RecursivelyGetAllTasksData(NestedJobTask, ref i, ref JobTasks, ref indentList, currentLevel + 1);
                }
            }
        }

        public List<TaskData> MergeTasks(List<TaskData> JobTasks, ref int i)
        {
            List<Car> mergedCars;
            for (int index = 1; index < JobTasks.Count; index++)
            {
                var currentTask = JobTasks[index];
                var previousTask = JobTasks[index - 1];

                if ((currentTask.type != TaskType.Sequential || currentTask.type != TaskType.Parallel) && (previousTask.type != TaskType.Sequential || previousTask.type != TaskType.Parallel))
                {
                    if (currentTask.type == TaskType.Warehouse && previousTask.type == TaskType.Warehouse)
                    {
                        if (currentTask.warehouseTaskType == WarehouseTaskType.Loading &&
                            previousTask.warehouseTaskType == WarehouseTaskType.Loading &&
                            currentTask.destinationTrack == previousTask.destinationTrack)
                        {
                            mergedCars = previousTask.cars.Union(currentTask.cars).ToList();
                            TaskData mergedTaskData = new TaskData(
                                currentTask.type,
                                currentTask.state,
                                currentTask.taskStartTime,
                                currentTask.taskFinishTime,
                                mergedCars,
                                currentTask.startTrack,
                                currentTask.destinationTrack,
                                currentTask.warehouseTaskType,
                                currentTask.cargoTypePerCar,
                                currentTask.totalCargoAmount,
                                null,
                                currentTask.couplingRequiredAndNotDone,
                                currentTask.anyHandbrakeRequiredAndNotDone
                            );
                            JobTasks.RemoveAt(index - 1);
                            JobTasks[index - 1] = mergedTaskData;
                            i--;
                        }
                        else if (currentTask.warehouseTaskType == WarehouseTaskType.Unloading &&
                            previousTask.warehouseTaskType == WarehouseTaskType.Unloading &&
                            currentTask.destinationTrack == previousTask.destinationTrack)
                        {
                            mergedCars = previousTask.cars.Union(currentTask.cars).ToList();
                            TaskData mergedTaskData = new TaskData(
                                currentTask.type,
                                currentTask.state,
                                currentTask.taskStartTime,
                                currentTask.taskFinishTime,
                                mergedCars,
                                currentTask.startTrack,
                                currentTask.destinationTrack,
                                currentTask.warehouseTaskType,
                                currentTask.cargoTypePerCar,
                                currentTask.totalCargoAmount,
                                null,
                                currentTask.couplingRequiredAndNotDone,
                                currentTask.anyHandbrakeRequiredAndNotDone
                            );
                            JobTasks.RemoveAt(index - 1);
                            JobTasks[index - 1] = mergedTaskData;
                            i--;
                        }
                    }
                }
            }
            return JobTasks;
        }

        public Tuple<List<TaskData>, List<int>> RebuiltTasksList(Job job)
        {
            int i = 0;
            var JobTasks = new List<TaskData>();
            var indentList = new List<int>();
            foreach (var jobTask in job.tasks)
            {
                RecursivelyGetAllTasksData(jobTask, ref i, ref JobTasks, ref indentList);
                for (int i1 = 0; i1 < JobTasks.Count; i1++)
                {
                    TaskData task = JobTasks[i1];
                }
                i -= 1;
                JobTasks = MergeTasks(JobTasks, ref i);
            }
            int remergeCheck = JobTasks.Count();
            while (remergeCheck > 6)
            {
                i = 0;
                JobTasks = MergeTasks(JobTasks, ref i);
                remergeCheck--;
            }
            Tuple<List<TaskData>, List<int>> output = new Tuple<List<TaskData>, List<int>>(JobTasks, indentList);
            return output;
        }

        public static StationController StationFromTrack(Track track)
        {
            var searchedTracks = new HashSet<Track>();
            if (track == null) return null;
            return FindStationRecursive(track, 0, searchedTracks);
        }

        private static StationController FindStationRecursive(Track track, int depth, HashSet<Track> searchedTracks)
        {
            if (track == null || searchedTracks.Contains(track)) return null;
            searchedTracks.Add(track);
            var yardId = track.ID?.yardId;
            if (yardId == null) return null;
            var station = StationController.GetStationByYardID(yardId);
            if (station != null) return station;
            if (yardId == "#Y" && depth < 10)
            {
                var connectedTracks = GetConnectedTracks(track);
                foreach (var connected in connectedTracks)
                {
                    var result = FindStationRecursive(connected, depth + 1, searchedTracks);
                    if (result != null)
                        return result;
                }
            }
            return null;
        }

        private static IEnumerable<Track> GetConnectedTracks(Track track)
        {
            var tracks = new List<Track>();
            if (track.InTrack != null) tracks.Add(track.InTrack);
            if (track.OutTrack != null) tracks.Add(track.OutTrack);
            if (track.PossibleInTracks != null) tracks.AddRange(track.PossibleInTracks);
            if (track.PossibleOutTracks != null) tracks.AddRange(track.PossibleOutTracks);
            return tracks;
        }

        public StationController GetCurrentStation(Job job)
        {
            var closestStation = StationController.allStations.OrderBy(s => ((StationJobGenerationRange)StationControllerStationRangeField.GetValue(s)).PlayerSqrDistanceFromStationCenter).FirstOrDefault();
            if (job.State == DV.ThingTypes.JobState.Available || job.State == DV.ThingTypes.JobState.Completed)
            {
                return closestStation;
            }
            else
            {
                Track track;
                StationController trainStation = null;
                foreach (var task in RebuiltTasksList(job).Item1)
                {
                    if (task.type == TaskType.Transport)
                    {
                        track = task.cars[0].CurrentTrack;
                        trainStation = StationFromTrack(track);
                        break;
                    }
                }
                return trainStation ?? closestStation;
            }
        }

        public string GetTaskOverview(Job job)
        {
            var currentYardID = GetCurrentStation(job).stationInfo.YardID;
            int i = 0;
            int indent;
            var TaskStringBuilder = new StringBuilder();
            var outputStringBuilder = new StringBuilder();

            foreach (var taskTaskData in RebuiltTasksList(job).Item1)
            {
                indent = RebuiltTasksList(job).Item2[i];
                TaskStringBuilder.Clear();

                switch (taskTaskData.type)
                {
                    case TaskType.Transport:
                        AppendIndented(indent, $"Transport {FormatNumberOfCars(taskTaskData.cars.Count)} from {FormatTrack(taskTaskData.startTrack, currentYardID)} to {FormatTrack(taskTaskData.destinationTrack, currentYardID)}", TaskStringBuilder);
                        break;
                    case TaskType.Warehouse:
                        if (taskTaskData.warehouseTaskType == WarehouseTaskType.Loading)
                        {
                            AppendIndented(indent, $"Load {FormatNumberOfCars(taskTaskData.cars.Count)} at {FormatTrack(taskTaskData.destinationTrack, currentYardID)}", TaskStringBuilder);
                        }
                        else if (taskTaskData.warehouseTaskType == WarehouseTaskType.Unloading)
                        {
                            AppendIndented(indent, $"Unload {FormatNumberOfCars(taskTaskData.cars.Count)} at {FormatTrack(taskTaskData.destinationTrack, currentYardID)}", TaskStringBuilder);
                        }
                        else
                        {
                            AppendIndented(indent, "(unknown WarehouseTaskType)", TaskStringBuilder);
                        }
                        break;
                    case TaskType.Parallel:
                        break;
                    case TaskType.Sequential:
                        break;
                    default:
                        AppendIndented(indent, "(unknown TaskType)", TaskStringBuilder);
                        break;
                }

                switch (taskTaskData.state)
                {
                    case TaskState.Done:
                        outputStringBuilder.Append(GetColoredString(Color.green, TaskStringBuilder.ToString()));
                        break;
                    case TaskState.Failed:
                        outputStringBuilder.Append(GetColoredString(Color.red, TaskStringBuilder.ToString()));
                        break;
                    default:
                        outputStringBuilder.Append(TaskStringBuilder.ToString());
                        break;
                }
                i++;
            }
            return outputStringBuilder.ToString();
        }

        private static string GetColoredString(Color color, string content)
        {
            var colorString = GetHexColorComponent(color.r) + GetHexColorComponent(color.g) + GetHexColorComponent(color.b);
            return $"<color=#{colorString}>{content}</color>";
        }

        private static string GetHexColorComponent(float colorComponent)
        {
            return ((int)(255 * colorComponent)).ToString("X2");
        }

        private string FormatTrack(Track track, string currentYardID)
        {
            if (track.ID.yardId == currentYardID)
            {
                return Main.Settings.ShowFullTrackIDs ? track.ID.FullID : track.ID.TrackPartOnly;
            }
            var trackDisplayID = Main.Settings.ShowFullTrackIDs ? track.ID.FullID : track.ID.FullDisplayID;
            if (_yardID2Color.TryGetValue(track.ID.yardId, out var color))
            {
                return GetColoredString(color, trackDisplayID);
            }
            return trackDisplayID;
        }

        private static string FormatNumberOfCars(int count)
        {
            return count == 1 ? "1 car" : count + " cars";
        }

        private static void AppendIndented(int indent, string value, StringBuilder sb)
        {
            for (var i = 0; i < indent; i += 1)
            {
                sb.Append("  ");
            }
            sb.Append("- ");
            sb.Append(value + Environment.NewLine);
        }
    }
}