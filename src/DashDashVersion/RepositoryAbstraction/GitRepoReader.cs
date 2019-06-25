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

        internal static GitRepoReader Load(
            string path,
            string currentBranch)
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
                        repoPath)), currentBranch);
        }

        internal GitRepoReader(
            IGitRepository repo,
            string currentBranchName)
        {
            _repository = repo;
            CurrentBranch = FindCurrentBranch(currentBranchName);
            var tags = VisibleTags(repo.Commits);
            var highestReleaseVersionTag = HighestReleaseVersionLowestPatchTag(tags);
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

        public uint CommitCountSinceBranchOffFromDevelop
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

        private static GitTag HighestReleaseVersionLowestPatchTag(IEnumerable<GitTag> tags)
        {
            var releaseTagsAndVersions = tags
                .Where(tag => Patterns.IsReleaseVersionTag.IsMatch(tag.FriendlyName))
                .Select(tag => (Tag: tag, VersionNumber: VersionNumber.Parse(tag.FriendlyName)));

            if (!releaseTagsAndVersions.Any())
                throw new InvalidOperationException($"There is no tag with in the '<major>.<minor>.<patch>' format in this repository looking from the HEAD down: {Patterns.IsReleaseVersionTag}.");

            var highestVersion = releaseTagsAndVersions
                .Select(pair => pair.VersionNumber)
                .Max();

            return releaseTagsAndVersions
                .Where(
                    pair => pair.VersionNumber.Major == highestVersion.Major && 
                    pair.VersionNumber.Minor == highestVersion.Minor)
                .OrderBy(pair => pair.VersionNumber)
                .First().Tag;
            
        }

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

        private static string TrimRemoteName(GitBranch branch)
        {
            if (!branch.IsRemote)
            {
                return branch.FriendlyName;
            }
            var splitLocation = branch.FriendlyName.IndexOf(Constants.BranchNameInfoDelimiter) + 1;
            return branch.FriendlyName.Substring(splitLocation);
        }

        private GitBranch FindBranch(string branchName)
        {
            var perfectMatch = _repository.Branches.FirstOrDefault(b => b.FriendlyName.Equals(branchName));
            if (perfectMatch != null)
            {
                return perfectMatch;
            }

            var branches = _repository.Branches.Where(b => b.FriendlyName.EndsWith(branchName) && Patterns.DetermineBranchType.IsMatch(b.FriendlyName));
            if (!branches.Any())
            {
                throw new ArgumentException($"The branch '{branchName}' could not be found in the repository, or it was not of any of the supported types.", nameof(branchName));
            }

            var distinctBranchTypes = branches.Select(b => Patterns.DetermineBranchType.Match(b.FriendlyName).Groups["branchType"].Captures[0]?.Value).Distinct();
            if (distinctBranchTypes.Count() > 1)
            {
                throw new ArgumentException($"This partial branch name: '{branchName}' is not unique in the repository.", nameof(branchName));
            }

            return branches.First();
        }

        private GitBranch FindCurrentGitBranch(string branchName) => 
            string.IsNullOrWhiteSpace(branchName) ? 
                BranchForRepositoryHead() : 
                FindBranch(branchName);

        private IEnumerable<GitTag> VisibleTags(
            IEnumerable<GitCommit> commits) =>
                _repository.Tags.Where(tag => commits.Any(commit => commit.Sha == tag.Sha));

        private static bool IsDevelop(GitBranch branch) =>
            branch.FriendlyName.Equals(Constants.DevelopBranchName);

        private static bool IsOriginDevelop(GitBranch branch) =>
            branch.IsRemote &&
            branch.FriendlyName.Equals(Constants.OriginDevelop);

        private GitBranch BranchForRepositoryHead() =>
            _repository.Branches
                .Where(f => f.IsCurrentRepositoryHead)
                .Select(f => f)
                .FirstOrDefault() ??
            throw new InvalidOperationException(
                "The repository is on a detached HEAD, please specify the name of the branch for which the version should be calculated using the --branch command-line argument.");

        private BranchInfo FindCurrentBranch(string branchName) =>
            BranchInfoFactory.CreateBranchInfo(
                TrimRemoteName(
                    FindCurrentGitBranch(branchName)));
    }
}