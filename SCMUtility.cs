// Copyright (c) iQubit Inc. All rights reserved.
// Use of this source code is governed by a GPL-v3 license

namespace SCMApp {
  using System;
  using LibGit2Sharp;
  using System.Threading.Tasks;

  /// <summary> Source Control Manager Application
  /// This name coz we might wanna support other version control systems in future
  /// </summary>
  internal static class NamespaceDoc { }

  internal class GitUtility {
    /// <summary>
    /// Action representing sequence of git commands
    /// <see href="https://stackoverflow.com/q/105372">
    /// enumeration reference: SO - How to enumerate an enum
    /// </see>
    /// </summary>
    public enum SCMAction {
      PushModified,
      UpdateRemote,
      DeleteBranch,
      ShowInfo,
      ShowStatus,
      Pull
    };

    /// <summary>
    /// Repository Instance
    /// </summary>
    private Repository Repo { get; set; }
    /// <summary>
    /// What Git Action to perform
    /// </summary>
    private SCMAction Action { get; set; }
    /// <summary>
    /// Configuration Instance
    /// </summary>
    private JsonConfig Config { get; set; }

    public GitUtility(SCMAction action, string repoPath, string filePath) {
      if (string.IsNullOrEmpty(repoPath))
        repoPath = System.IO.Directory.GetCurrentDirectory();
      else if (! System.IO.Directory.Exists(repoPath))
        throw new ArgumentException("Provide repository path does not exist!");

      try {
        Repo = new Repository(repoPath);
      }
      catch (RepositoryNotFoundException e) {
        Console.WriteLine("Repo dir: " + repoPath);
        Console.WriteLine(e.Message);
        throw ;
      }
 
      Action = action;
      
      Config = null;
    }

    /// <summary>
    /// Get short commit sha id: 9 alpha digits
    /// Truncate the tip of the Head
    /// </summary>
    private string GetShaShort() {
      if (Repo.Head.Tip == null) {
        Console.WriteLine("Head doesn't exist yet! Yet to do a first commit and create branch?");
        return string.Empty;
      }
      return Repo.Head.Tip.Id.ToString().Substring(0, 9); 
    }


    /// <summary>
    /// Get rid of the suffix from git repo path
    /// <example>
    /// Otherwise output string comes as following,
    ///  "D:\Code\CoolApp\.git\"
    /// After trimming suffix it becomes,
    ///  "D:\Code\CoolApp"
    /// </example>
    /// </summary>
    private string GetRepoPath() {
      var repoPath = Repo.Info.WorkingDirectory;
      if (repoPath == null)
        throw new NotImplementedException($"Most likely a bare repository: {Repo.Info.Path}. It is" + 
        " not tested with this app yet.");

      return repoPath.Substring(0, repoPath.Length-1);
    }


    public void ShowRepoAndUserInfo() {
      Console.WriteLine("Local Repo: " + GetRepoPath());
      var username = Repo.Config.Get<string>("user.name").Value;
      Console.WriteLine("Author: " + username);
      var email = Repo.Config.Get<string>("user.email").Value;
      Console.WriteLine("Email: " + email);
      Console.WriteLine("Branch: " + Repo.Head);
      Console.WriteLine("SHA: " + GetShaShort());
    }

    private async Task InstantiateJsonConfig() {
      if (Config == null) {
        // TODO: merge these 2 into constructor or something
        Config = new JsonConfig();
        await Config.Load(Repo.Config.Get<string>("user.name").Value, Repo.Config.
          Get<string>("user.email").Value, GetRepoPath());
      }
    }

    public async Task ShowStatus() {
      ShowRepoAndUserInfo();

      var statusOps = new StatusOptions();
      statusOps.IncludeIgnored = false;

      Console.WriteLine(Environment.NewLine + "Local changes:");
      foreach (var item in Repo.RetrieveStatus(statusOps))
        Console.WriteLine((item.State == FileStatus.ModifiedInWorkdir? "*": " ") + " " + item.FilePath);

      Console.WriteLine(Environment.NewLine + "Message (to be used with next commit):");
      Console.WriteLine(await GetCommitMessage(singleLine: true));
      Console.WriteLine("..." + Environment.NewLine);
    }

    /// <summary>
    /// Get commit message from commit log file (full or first line of it)
    /// <see href="https://stackoverflow.com/q/52598516">SO - Read first line of a text file C#</see>
    /// </summary>
    /// <param name="singleLine">whether to return only first line</param>
    /// <returns>retrieved message</returns>
    private async Task<string> GetCommitMessage(bool singleLine = false) {
      await InstantiateJsonConfig();
      var commitFilePath = Config.GetCommitFilePath();

      if (!System.IO.File.Exists(commitFilePath))
        throw new InvalidOperationException($"Log file: {commitFilePath} not found!");

      if (singleLine)
        // Open the file to read from
        using (System.IO.StreamReader sr = System.IO.File.OpenText(commitFilePath)) {
            return sr.ReadLine();;
        }
      else
        return System.IO.File.ReadAllText(commitFilePath);
    }

    /// <summary>
    /// Pull changes from remote
    /// </summary>
    /// <remarks>
    /// does not support pull from private repository
    ///  TODO: test with private
    /// </remarks>
    public async Task PullChanges(bool isRemoteUpstream) {
      // will be rquired if repository requires authentication
      // await InstantiateJsonConfig();
      var options = new PullOptions() {
        FetchOptions = new FetchOptions() /*{
          CredentialsProvider = Config.GetCredentials(),
        },*/
      };

      var signature = Repo.Config.BuildSignature(DateTimeOffset.Now);
      // TODO: add an optional argument to provide this
      var remoteBranchName = "main";    // usually main for GitHub etc.

      try {
        if (! isRemoteUpstream)
          Commands.Pull(Repo, signature, options);
        else {
          var remoteName = "upstream";
          var refSpec = string.Format("refs/heads/{2}:refs/remotes/{0}/{1}", remoteName, Repo.Head.FriendlyName, remoteBranchName);
          await InstantiateJsonConfig();
          Console.WriteLine($"pulling {remoteName}/{remoteBranchName}");

          // Perform the actual fetch
          Commands.Fetch(Repo, remoteName, new string[] { refSpec },
            options.FetchOptions,
            null
          );
          // Merge fetched refs
          Repo.MergeFetchedRefs(signature, options.MergeOptions);
        }
      }
      catch (CheckoutConflictException e) {
          Console.WriteLine(e.Message);
          return ;
      }
      catch (LibGit2Sharp.MergeFetchHeadNotFoundException e) {
        Console.WriteLine($"{remoteBranchName} does not exist! " + e.Message);
        return ;
      }

      Console.WriteLine($"{Repo.Head} -> {GetShaShort()}");
    }

    /// <summary>
    /// At present 3 types are supported
    /// - single: only specified file
    /// - Update: indicates only modified files
    /// - all files in the repository workspace
    /// </summary>
    public enum StageType {
      Single,
      Update,
      All
    };


    /// <summary>
    /// Stage changes for committing
    /// </summary>
    /// <remarks>
    /// Provides hardcoded relative path support for 'input\posts'
    /// </remarks>
    /// <param name="stageType"><see cref="StageType"/></param>
    /// <param name="filePath">file path passed with 'push single'</param>
    /// <returns>indicates whether any change was staged</returns>
    private bool Stage(StageType stageType, string filePath) {
      bool isModified = false;
      var statusOps = new StatusOptions{IncludeIgnored = false};

      switch(stageType) {
      case StageType.Single:
        if (string.IsNullOrEmpty(filePath) == false)
        {
          var repoPath = GetRepoPath();
          // Get relative path of the dir
          if (filePath.StartsWith(repoPath))
            filePath = filePath.Substring(repoPath.Length + 1);

          var statiqPostsPath = @"input\posts";
          if (System.IO.Directory.Exists(statiqPostsPath) && filePath.EndsWith(".md") && !filePath
              .StartsWith(statiqPostsPath)) {
            var newFilePath = statiqPostsPath + '\\' + filePath;
            if (System.IO.File.Exists(newFilePath))
              filePath = newFilePath;
          }
        }

        var isDir = System.IO.Directory.Exists(filePath);
        if (System.IO.File.Exists(filePath) || isDir) {
          Console.WriteLine((isDir? "* d " : "* ") + filePath);
          Repo.Index.Add(filePath);
          Repo.Index.Write();
          isModified = true;
        }
        else // Logger Verbose
          Console.WriteLine($"{filePath} doesn't exist!");
        break;

      case StageType.Update:
        foreach (var item in Repo.RetrieveStatus(statusOps)) {
            // Stage file if it's modified
            if (item.State == FileStatus.ModifiedInWorkdir)
            {
              if (System.IO.File.Exists(item.FilePath)) {
                Console.WriteLine("* " + item.FilePath);
                Repo.Index.Add(item.FilePath);
                Repo.Index.Write();
              }

              if (!isModified)
                isModified = true;
            }
        }
        break;
      
      case StageType.All:
        foreach (var item in Repo.RetrieveStatus(statusOps)) {
            // Stage any file found
            if (System.IO.File.Exists(item.FilePath)) {
              Console.WriteLine("* " + item.FilePath);
              Repo.Index.Add(item.FilePath);
              Repo.Index.Write();
            }

            if (!isModified)
              isModified = true;
        }
        break;

      default:
        break;
      }

      return isModified;
    }

    /// <summary>
    /// Commit staged changes
    /// - create the committer's signature
    /// - use that signature to commit
    /// * Get Signature from Repo Config ref,
    /// * And, Amend ref,
    /// <see href="LibGit2Sharp.Tests/CommitFixture.cs">LibGit2Sharp CommitFixture Tests</see>
    /// </summary>
    private async Task Commit(bool shouldAmend = false) {
      Signature signature = Repo.Config.BuildSignature(DateTimeOffset.Now);

      // Commit to local repository
      Console.WriteLine("author name: " + signature.Name);

      Commit commit = Repo.Commit(await GetCommitMessage(), signature, signature,
        new CommitOptions { AmendPreviousCommit = shouldAmend });

      // Use Logger Verbose
      Console.WriteLine("committed with amend flag: " + shouldAmend);
      Console.WriteLine("and message:" + Environment.NewLine
        + await GetCommitMessage(singleLine: true) + Environment.NewLine);

      // Test this with an initialized repo (no commits)
      //  ref, https://github.com/libgit2/libgit2sharp/issues/802
      var branches = new EnumerableType<Branch>(Repo.Branches.GetEnumerator());
      var branch = branches.First();
      
      if (branch == null) {
        // should trigger right after a 'git init'
        branch = Repo.Branches.Add("dev", commit);
        // Update the HEAD reference to point to the latest commit
        Repo.Refs.UpdateTarget(Repo.Refs.Head, commit.Id);
      }
    }

    /// <summary>
    /// Generic class to support Generic Type to return First Item when an IEnumerable is
    /// provided
    /// </summary>
    private class EnumerableType<T> {
      private System.Collections.Generic.IEnumerator<T> Iter;

      public EnumerableType(System.Collections.Generic.IEnumerator<T> iter) {
        Iter = iter;
      }

      public T First() {
        var first = default(T);
        
        if (Iter.MoveNext())
          first = Iter.Current;

        return first;
      }
    }

    private string GetCommitMessageFromFirst() {
      var commits = new EnumerableType<Commit>(Repo.Commits.GetEnumerator());
      var lastCommit = commits.First();
      return lastCommit?.Message;
    }

    private async Task<bool> HasCommitLogChanged() {
      var rMsg = GetCommitMessageFromFirst();

      // Logger Verbose
      if (rMsg == null || rMsg == string.Empty)
        Console.WriteLine("failed to retrieve commit message!");

      var lMsg = await GetCommitMessage();

      // Logger Verbose
      // Console.WriteLine("comparison result: " + (rMsg.Trim() != lMsg.Trim()));
      return rMsg.Trim() != lMsg.Trim();
    }

    private void OnPushStatusError(PushStatusError pushStatusErrors) {
      Console.WriteLine(string.Format("Failed to update reference '{0}': {1}",
          pushStatusErrors.Reference, pushStatusErrors.Message));
    }

    /// <summary>
    /// Push commits to remote
    ///  does force push when --amend flag is present
    /// <remarks>
    /// For a brand new git repository just initialized with no head and no branch
    ///  set name of the branch to be created as 'dev'
    ///
    /// - push ref spec primary attribution,
    ///  <see href="https://git-scm.com/book/en/v2/Git-Internals-The-Refspec">Git Refspec</see>
    ///
    /// - pertinent to push ref spec used in Network.Push
    /// <see href="https://stackoverflow.com/q/47294514">
    ///  SO - libgit2sharp Git cannot push non-fastforwardable reference
    /// </see>
    /// </remarks>
    /// </summary>
    private async Task PushToRemote(bool shouldForce = false) {
      var targetBranch = Repo.Head.FriendlyName;
      // Use Logger Verbose
      var originBranchStr = "origin/" + targetBranch;
      Console.WriteLine("origin branch string: " + originBranchStr + " does Remote Origin Target " +
        "Branch Exist: " + (Repo.Branches[originBranchStr] != null));

      LibGit2Sharp.Handlers.PackBuilderProgressHandler packBuilderCb = (x, y, z) => {
        Console.WriteLine($"{x} {y} {z}");
        return true;
      };

      // Config is not instantiated if commit was not called
      await InstantiateJsonConfig();
      var options = new PushOptions() {
          CredentialsProvider = Config.GetCredentials(),
          OnPushStatusError = OnPushStatusError,
          OnPackBuilderProgress = packBuilderCb
        };

      try {
        var formatSpec = (shouldForce || Repo.Branches[originBranchStr] == null)? "+{0}:{0}" : "{0}";
        var pushRefSpec = string.Format(formatSpec, Repo.Head.CanonicalName);
        Console.WriteLine("refSpec: " + pushRefSpec);

        var remote = Repo.Network.Remotes["origin"];
        if (remote == null) {
          Console.WriteLine("Exception: Remote origin not found! Try running with set-url argument.");
          throw new LibGit2SharpException("Remote origin not found!");
        }
        Console.WriteLine("remote name: " + remote.Name);

        Repo.Network.Push(remote, pushRefSpec, options);
      }
      catch (System.NullReferenceException) {
        Console.WriteLine("Unexpected, since branch is not hardcoded!");
        return ;
      }
      catch (NonFastForwardException) {
        Console.WriteLine("Attempting fast forward with force flag: " + shouldForce +
          " failed! Consider passing --amend");
        return ;
      }
      catch(LibGit2SharpException e) {
        Console.WriteLine("Exception: {0}", e.Message + (e.InnerException != null ? " / " + e.InnerException.Message : ""));

        // stuffs to help debugging
        Console.WriteLine("Canonical name: " + Repo.Branches[targetBranch].CanonicalName);

        var remote = Repo.Network.Remotes["origin"];
        Console.WriteLine("URL: " + remote.Url);
        Console.WriteLine("Push URL: " + remote.PushUrl);
        return ;
      }
      catch (Exception ex) {
          Console.WriteLine("Exception: {0}", ex.Message + (ex.InnerException != null ? " / " + ex.InnerException.Message : ""));
      }

      Console.Write("pushed" + (shouldForce? " (forced) " : " ") + "-> " + GetShaShort());
    }

    /// <summary>
    /// Wire the cases to methodologically call Stage()
    /// </summary>
    private bool StageHelper(string filePath) {
      if (string.IsNullOrEmpty(filePath))
        return Stage(StageType.Update, string.Empty);
      if (filePath == "__cmdOption:--all")
        return Stage(StageType.All, string.Empty);
      return Stage(StageType.Single, filePath);
    }

    /// <summary>
    /// SCP - Stage, Commit and Push
    /// </summary>
    /// <param name="filePath">file path passed with 'push single', Empty otherwise</param>
    /// <param name="shouldAmend">amend commit and force push</param>
    public async Task SCPChanges(string filePath, bool shouldAmend = false) {
      var isMod = StageHelper(filePath);
      if (isMod)
        Console.WriteLine("changes staged");

      if (isMod || (shouldAmend && await HasCommitLogChanged()))
        await Commit(shouldAmend);
      await PushToRemote(shouldForce: shouldAmend);
    }

    /// <summary>
    /// Add/update remote URL
    /// </summary>
    /// <param name="remoteName">remote name i.e., origin or upstream</param>
    /// <param name="remoteURL">remote's URL</param>
    public void UpdateRemoteURL(string remoteName, string remoteURL) {
      if (Repo.Network.Remotes[remoteName] == null)
        Repo.Network.Remotes.Add(remoteName, remoteURL);
      else if (Repo.Network.Remotes[remoteName].Url == remoteURL &&
        Repo.Network.Remotes[remoteName].PushUrl == remoteURL)
      {
        Console.WriteLine("Already set to provided URL!");
      }
      else {
        Repo.Network.Remotes.Update(remoteName, r => r.Url = remoteURL);
        Repo.Network.Remotes.Update(remoteName, r => r.PushUrl = remoteURL);
      }
    }

    /// <summary>
    /// Delete specified branch from local and remote
    ///  Use the Push Refspec till API support for remote deletion is found
    ///
    /// - push ref spec primary attribution,
    ///  <see href="https://git-scm.com/book/en/v2/Git-Internals-The-Refspec">Git Refspec</see>
    /// </summary>
    /// <param name="branchName">branch name</param>
    public async Task DeleteBranch(string branchName) {
      if (Repo.Branches[branchName] == null) {
        Console.WriteLine($"Branch {branchName} does not exist!");
        return ;
      }

      Console.WriteLine($"Branch {branchName}");

      var formatSpecDelRBranch = ":{0}";
      var pushRefSpec = string.Format(formatSpecDelRBranch, Repo.Branches[branchName].CanonicalName);

      // instantiate config
      await InstantiateJsonConfig();
      var options = new PushOptions() { CredentialsProvider = Config.GetCredentials() };

      Repo.Network.Push(Repo.Network.Remotes["origin"], pushRefSpec, options);
      Console.WriteLine("- removed from remote (NoOp if already removed)");

      Repo.Branches.Remove(branchName, false);
      Console.WriteLine("- removed from local");
    }
  }
}
