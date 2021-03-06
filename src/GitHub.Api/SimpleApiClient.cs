﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Primitives;
using GitHub.Services;
using Octokit;

namespace GitHub.Api
{
    public class SimpleApiClient : ISimpleApiClient
    {
        public HostAddress HostAddress { get; }
        public UriString OriginalUrl { get; }

        readonly IGitHubClient client;
        readonly Lazy<IEnterpriseProbeTask> enterpriseProbe;
        readonly Lazy<IWikiProbe> wikiProbe;
        static readonly SemaphoreSlim sem = new SemaphoreSlim(1);

        Repository repositoryCache = new Repository();
        string owner;
        bool? isEnterprise;
        bool? hasWiki;

        public SimpleApiClient(HostAddress hostAddress, UriString repoUrl, IGitHubClient githubClient,
            Lazy<IEnterpriseProbeTask> enterpriseProbe, Lazy<IWikiProbe> wikiProbe)
        {
            HostAddress = hostAddress;
            OriginalUrl = repoUrl;
            client = githubClient;
            this.enterpriseProbe = enterpriseProbe;
            this.wikiProbe = wikiProbe;
        }

        public async Task<Repository> GetRepository()
        {
            // fast path to avoid locking when the cache has already been set
            // once it's been set, it's never going to be touched again, so it's safe
            // to read. This way, lock queues will only form once on first load
            if (owner != null)
                return repositoryCache;
            return await GetRepositoryInternal();
        }

        async Task<Repository> GetRepositoryInternal()
        {
            await sem.WaitAsync();
            try
            {
                if (owner == null && OriginalUrl != null)
                {
                    var ownerLogin = OriginalUrl.Owner;
                    var repositoryName = OriginalUrl.RepositoryName;

                    if (ownerLogin != null && repositoryName != null)
                    {
                        var repo = await client.Repository.Get(ownerLogin, repositoryName);
                        if (repo != null)
                        {
                            hasWiki = await HasWikiInternal(repo);
                            isEnterprise = await IsEnterpriseInternal();
                            repositoryCache = repo;
                        }
                        owner = ownerLogin;
                    }
                }
            }
            // it'll throw if it's private
            catch {}
            finally
            {
                sem.Release();
            }

            return repositoryCache;
        }

        public bool HasWiki()
        {
            return hasWiki.HasValue && hasWiki.Value;
        }

        public bool IsEnterprise()
        {
            return isEnterprise.HasValue && isEnterprise.Value;
        }

        async Task<bool> HasWikiInternal(Repository repo)
        {
            if (repo == null)
                return false;

            if (!repo.HasWiki)
            {
                hasWiki = false;
                return false;
            }

            var probe = wikiProbe.Value;
            Debug.Assert(probe != null, "Lazy<Wiki> probe is not set, something is wrong.");
#if !DEBUG
            if (probe == null)
                return false;
#endif
            var ret = await probe.ProbeAsync(repo);
            return (ret == WikiProbeResult.Ok);
        }

        async Task<bool> IsEnterpriseInternal()
        {
            var probe = enterpriseProbe.Value;
            Debug.Assert(probe != null, "Lazy<Enterprise> probe is not set, something is wrong.");
#if !DEBUG
            if (probe == null)
                return false;
#endif
            var ret = await probe.ProbeAsync(HostAddress.WebUri);
            return (ret == EnterpriseProbeResult.Ok);
        }
    }
}
