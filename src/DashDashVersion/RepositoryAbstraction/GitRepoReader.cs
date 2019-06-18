﻿// Copyright 2019 Hightech ICT and authors

// This file is part of DashDashVersion.

// DashDashVersion is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// DashDashVersion is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU Lesser General Public License for more details.

// You should have received a copy of the GNU Lesser General Public License
// along with DashDashVersion. If not, see<https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace DashDashVersion.RepositoryAbstraction
{
    /// <summary>
    /// This class uses LibGet2Sharp to read the information from the git repository needed to determine a version number.
    /// </summary>
    internal sealed class GitRepoReader : IGitRepoReader
    {
        private readonly IGitRepository _repository;

        internal static GitRepoReader Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("The path should not be null or empty.", nameof(path));
            }
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                throw new ArgumentException($"The path '{path}' does not exist.", nameof(path));
            }

            var repoPath = Repository.Discover(path);
            if (string.IsNullOrEmpty(repoPath))
            {
                throw new ArgumentException($"The path: '{path}' is not the root of or in a git repository.", nameof(path));
            }
            return new GitRepoReader(
                GitRepository.FromRepository(
                    new Repository(
                        repoPath)));
        }

        internal GitRepoReader(IGitRepository repo)
        {
            _repository = repo;
            GitBranch branch;
            CurrentBranch = FindCurrentBranch(out branch);
            var tags = repo.Tags.Where(tag => branch.Commits.Any(commit => commit.Sha == tag.Sha));
            var highestReleaseVersionTag = HighestReleaseVersionTag(tags);
            CurrentReleaseVersion = VersionNumber.Parse(highestReleaseVersionTag.FriendlyName);
            CommitCountSinceLastReleaseVersion = FindAncestor(
                highestReleaseVersionTag.Sha,
                _repository.Commits);
            var hash = _repository.Commits.FirstOrDefault();
            HeadCommitHash = hash?.Sha ?? throw new InvalidOperationException("Git repositories without commits are not supported.");
        }

        public string HeadCommitHash { get; }

        public VersionNumber CurrentReleaseVersion { get; }

        public BranchInfo CurrentBranch { get; }
        public uint CommitCountSinceLastReleaseVersion { get; }

        public VersionNumber? HighestMatchingTagForReleaseCandidate
        {
            get
            {
                if (!(CurrentBranch is ReleaseCandidateBranchInfo currentRc))
                {
                    throw new InvalidOperationException(
                        $"Determining the highest matching tag for release candidates is only possible if the current branch is a 'release' branch, the current branch is'{CurrentBranch}'.");
                }
                var toCheckFor =
                    $"{currentRc.VersionFromName}{Constants.PreReleaseLabelDelimiter}{Constants.ReleasePreReleaseLabel}";
                var returnTag = HighestMatchingTag(_repository.Tags, toCheckFor);
                return returnTag == null ? null : VersionNumber.Parse(returnTag);
            }
        }

        public uint CommitCountSinceBranchOfFromDevelop
        {
            get
            {
                var developCommits = OriginDevelopOrDevelopCommits(_repository.Branches).ToArray();
                uint toReturn = 0;
                foreach (var commit in _repository.Commits)
                {
                    if (developCommits.Any(developCommit => commit.Sha.Equals(developCommit.Sha)))
                    {
                        return toReturn;
                    }

                    toReturn++;
                }
                throw new InvalidOperationException(
                    $"Git repository does not contain a common ancestor between '{Constants.DevelopBranchName}' and the current HEAD.");
            }
        }

        private static string? HighestMatchingTag(
            IEnumerable<GitTag> tags,
            string toMatch) =>
                tags
                    .Where(tag => Patterns.ValidVersionNumber.IsMatch(tag.FriendlyName))
                    .Where(tag => tag.FriendlyName.StartsWith(toMatch))
                    .OrderByDescending(t => VersionNumber.Parse(t.FriendlyName))
                    .FirstOrDefault()?.FriendlyName;

        private static IEnumerable<GitCommit> OriginDevelopOrDevelopCommits(IEnumerable<GitBranch> branches)
        {
            var develop = branches.FirstOrDefault(IsOriginDevelop);
            if (develop == null)
            {
                // ReSharper disable once PossibleMultipleEnumeration
                develop = branches.FirstOrDefault(IsDevelop);
                if (develop == null)
                {
                    throw new InvalidOperationException(
                        $"Git repository does not contain a branch named '{Constants.DevelopBranchName}' or '{Constants.OriginDevelop}'.");
                }
            }

            var developCommits = develop.Commits;
            if (!developCommits.Any())
            {
                throw new InvalidOperationException(
                    $"Git repository does not contain any commits on '{Constants.DevelopBranchName}' or '{Constants.OriginDevelop}'.");
            }

            return developCommits;
        }

        private static bool IsDevelop(GitBranch branch) =>
            branch.FriendlyName.Equals(Constants.DevelopBranchName);

        private static bool IsOriginDevelop(GitBranch branch) =>
            branch.IsRemote &&
            branch.FriendlyName.Equals(Constants.OriginDevelop);

        private static GitTag HighestReleaseVersionTag(IEnumerable<GitTag> tags) =>
            tags.Where(
                    tag => Patterns.IsReleaseVersionTag.IsMatch(tag.FriendlyName))
                .OrderByDescending(
                    t => VersionNumber.Parse(t.FriendlyName))
                .FirstOrDefault() ??
            throw new InvalidOperationException($"There is no tag with in the '<major>.<minor>.<patch>' format in this repository looking from the HEAD down: {Patterns.IsReleaseVersionTag}.");

        private static uint FindAncestor(
            string sha,
            IEnumerable<GitCommit> commits)
        {
            var matches = commits.Select(
                    (commit, index) => (commit, index))
                .Where(tuple => tuple.commit.Sha == sha).ToList();
            if (matches.Any())
            {
                return (uint)matches.First().index;
            }
            throw new ArgumentException($"No commit found with sha: '{sha}'.", nameof(sha));
        }

        private BranchInfo FindCurrentBranch(out GitBranch branch)
        {
            string branchName;
            branch = _repository.Branches
                .Where(f => f.IsCurrentRepositoryHead)
                .Select(f => f).FirstOrDefault();

            if (branch == null)
            {
                var currentCommit = _repository.Commits.FirstOrDefault();
                if (currentCommit == null)
                {
                    throw new InvalidOperationException("The repository is on a detached HEAD, and no commits where found.");
                }
                var matchingBranches = _repository.Branches.Where(branch => branch.Commits.Any(c => c.Sha.Equals(currentCommit.Sha)));
                var count = matchingBranches.Count();
                if (count == 0)
                {
                    throw new InvalidOperationException("The repository is on a detached HEAD, and the current commit is not present in any of the branches.");
                }
                if (count > 1)
                {
                    throw new InvalidOperationException("The repository is on a detached HEAD, and the current commit is on multiple branches.");
                }
                branch = matchingBranches.First();
            }

            if (branch.IsRemote)
            {
                var splitLocation = branch.FriendlyName.IndexOf(Constants.BranchNameInfoDelimiter);
                splitLocation++;
                branchName = branch.FriendlyName.Substring(splitLocation);
            }
            else
            {
                branchName = branch.FriendlyName;
            }

            return BranchInfoFactory.CreateBranchInfo(branchName);
        }
    }
}