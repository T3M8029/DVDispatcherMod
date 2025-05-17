using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DVDispatcherMod.DispatcherHints;
using DVDispatcherMod.DispatcherHintShowers;
using DVDispatcherMod.PlayerInteractionManagers;
using JetBrains.Annotations;

namespace DVDispatcherMod.DispatcherHintManagers {
    public sealed class DispatcherHintManager : IDisposable {
        private readonly IPlayerInteractionManager _playerInteractionManager;
        private readonly IDispatcherHintShower _dispatcherHintShower;
        private readonly TaskOverviewGenerator _taskOverviewGenerator;

        private int _counterValue;

        public DispatcherHintManager([NotNull] IPlayerInteractionManager playerInteractionManager, [NotNull] IDispatcherHintShower dispatcherHintShower, TaskOverviewGenerator taskOverviewGenerator) {
            _playerInteractionManager = playerInteractionManager;
            _dispatcherHintShower = dispatcherHintShower;
            _taskOverviewGenerator = taskOverviewGenerator;

            _playerInteractionManager.JobOfInterestChanged += HandleJobObInterestChanged;
        }

        public void SetCounter(int counterValue) {
            _counterValue = counterValue;
            UpdateDispatcherHint();
        }

        private void HandleJobObInterestChanged() {
            UpdateDispatcherHint();

            if (Main.Settings.EnableDebugLoggingOfJobStructure) {
                var job = _playerInteractionManager.JobOfInterest;
                if (job != null) {
                    DebugOutputJobWriter.DebugOutputJob(job);
                }
            }
        }

        private void UpdateDispatcherHint() {
            var currentHint = GetCurrentDispatcherHint();
            _dispatcherHintShower.SetDispatcherHint(currentHint);

            var loco = PlayerManager.LastLoco;
            if (loco != null) {
                var locoLocationHint = GetLocoLocationHint(loco);
                _dispatcherHintShower.SetLocoLocationHint(locoLocationHint);
            } else {
                _dispatcherHintShower.SetLocoLocationHint(null);
            }
        }

        private DispatcherHint GetCurrentDispatcherHint() {
            var job = _playerInteractionManager.JobOfInterest;
            if (job != null) {
                return new JobDispatch(job, _taskOverviewGenerator).GetDispatcherHint(_counterValue);
            } else {
                return null;
            }
        }

        public void Dispose() {
            _dispatcherHintShower.Dispose();

            _playerInteractionManager.JobOfInterestChanged -= HandleJobObInterestChanged;
            _playerInteractionManager.Dispose();
        }

        private string GetLocoLocationHint(TrainCar loco) {
            if (loco.derailed) {
                return "derailed";
            }

            var track = loco.FrontBogie.track;
            var position = loco.FrontBogie.traveller.Span;
            var length = loco.FrontBogie.track.GetKinkedPointSet().span;

            var basicInfo = $"{track.LogicTrack().ID.FullDisplayID} {position:F2} / {length:F2}";

            var foundTrackWithDistanceOrNull = Search(track.LogicTrack(), position);
            if (foundTrackWithDistanceOrNull != null) {
                return $"{basicInfo}{Environment.NewLine}{foundTrackWithDistanceOrNull.Value.Track.ID.FullDisplayID} {foundTrackWithDistanceOrNull.Value.Distance}{Environment.NewLine} Distance from staation is {(loco.transform.position - StationController.GetStationByYardID(foundTrackWithDistanceOrNull.Value.Track.ID.yardId).transform.position).sqrMagnitude}";
            }

            return basicInfo;
        }

        private (Track Track, double Distance)? Search(Track track, double position) {
            int searchIterations = 0;
            //const int maxSearchIterations = 120;
            var maxSearchIterations = Main.Settings.MaxSearchIterations;
            if (track == null) return null;
            Main.ModEntry.Logger.Log("Starting search for yard track with start at: " + track.ID.FullID);
            var yardTracksAndDistance = new List<(Track track, double distance)>();
            if (!track.ID.IsGeneric())
            {
                return (track, 0);
            }

            var fakeMinHeap = new List<(TrackSide TrackSide, double Distance)>();
            var visited = new HashSet<TrackSide>();

            fakeMinHeap.Add((new TrackSide { Track = track, IsStart = true }, position));
            fakeMinHeap.Add((new TrackSide { Track = track, IsStart = false }, track.length - position));
            fakeMinHeap.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            while ((fakeMinHeap.Any()) && (searchIterations <= maxSearchIterations)) //the search needs to be clamped by something otherwise it could in some cases go to thousands
            {
                searchIterations++;
                var (currentTrackSide, currentDistance) = fakeMinHeap.First();
                Main.ModEntry.Logger.Log($"Search iteration {searchIterations - 1} at track {currentTrackSide.Track.ID.FullID} with distance {currentDistance}");
                fakeMinHeap.RemoveAt(0);
                if (!visited.Contains(currentTrackSide))
                {
                    visited.Add(currentTrackSide);

                    if (!currentTrackSide.Track.ID.IsGeneric())
                    {
                        yardTracksAndDistance.Add((currentTrackSide.Track, currentDistance));
                    }

                    var connectedTrackSides = GetConnectedTrackSides(currentTrackSide);
                    foreach (var connectedTrackSide in connectedTrackSides)
                    {
                        fakeMinHeap.Add((connectedTrackSide, currentDistance));
                    }
                    //do not add same track twice
                    if (!fakeMinHeap.Any(ts => ts.TrackSide.Track == currentTrackSide.Track )) fakeMinHeap.Add((currentTrackSide with { IsStart = !currentTrackSide.IsStart }, currentDistance + currentTrackSide.Track.length));
                    fakeMinHeap.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                }
            }

            foreach (var TaD in yardTracksAndDistance.Where(TaD => TaD.distance > Main.Settings.MaxTrackDistance).ToList()) 
            {
                Main.ModEntry.Logger.Log($"Track distance for {TaD.track.ID.FullID} too big, skipping");
                yardTracksAndDistance.Remove(TaD);
            }

            if (yardTracksAndDistance.Count == 0)
            {
                Main.ModEntry.Logger.Warning("No yard tracks found within search depth.");
                return null;
            }

            // Remove military tracks that are far and return the closest yard track by searched path length
            var farAwayMilTracks = new HashSet<(Track track, double distance)>();
            yardTracksAndDistance.RemoveAll(consideredTrack =>
            {
                Main.ModEntry.Logger.Log($"Possible track {consideredTrack.track.ID.FullID} at length {consideredTrack.distance}");
                bool isFarMil = (consideredTrack.track.ID.yardId == "HMB" || consideredTrack.track.ID.yardId == "MFMB") && consideredTrack.distance > Main.Settings.MilTrackDistance; //value needs tweaking -- 400 seems fine for mil yard influence
                if (isFarMil)
                {
                    farAwayMilTracks.Add(consideredTrack);
                }
                return isFarMil;
            });

            if (yardTracksAndDistance.Count == 0 && farAwayMilTracks.Count > 0)
            {
                var fallback = farAwayMilTracks.OrderBy(t => t.distance).First();
                Main.ModEntry.Logger.Log($"Returning military track {fallback.track.ID.FullID} at length {fallback.distance} since no other tracks are close enough");
                return fallback;
            }
            var pickedTrack = yardTracksAndDistance.OrderBy(t => t.distance).First();
            Main.ModEntry.Logger.Log($"Picked track {pickedTrack.track.ID.FullID} at lenght {pickedTrack.distance}");
            return pickedTrack; 
        }

        private List<TrackSide> GetConnectedTrackSides(TrackSide currentTrackSide) {
            var railTrack = currentTrackSide.Track.RailTrack();
            var branches = currentTrackSide.IsStart ? railTrack.GetAllInBranches() : railTrack.GetAllOutBranches();
            if (branches == null) {
                return new List<TrackSide>();
            }
            return branches.Select(b => new TrackSide { Track = b.track.LogicTrack(), IsStart = b.first }).ToList();
        }


        public record TrackSide {
            public Track Track { get; set; }
            public bool IsStart { get; set; }
        }
    }
}