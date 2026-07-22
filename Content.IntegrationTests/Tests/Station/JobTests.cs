using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Prototypes;
using System.Collections.Generic;
using System.Linq;

namespace Content.IntegrationTests.Tests.Station;

[TestFixture]
[TestOf(typeof(SharedJobSystem))]
public sealed class JobTest
{
    private static readonly HashSet<ProtoId<JobPrototype>> MultiDepartmentJobs =
    [
        "DirectorOfCare",
        "Brigmedic",
        "NFDetective",
        "Sheriff",
        "Bailiff",
        "BlueShieldOfficer",
        "SeniorOfficer",
        "SDEngineer",
        "Deputy",
        "Cadet",
        "HeadOfPrison",
        "SecurityGuard",
    ];

    /// <summary>
    /// Ensures that jobs belong to at most one primary department unless the overlap is intentional.
    /// Having no primary department is ok.
    /// </summary>
    [Test]
    public async Task PrimaryDepartmentsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var prototypeManager = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            // only checking primary departments so don't bother with others
            var departments = prototypeManager.EnumeratePrototypes<DepartmentPrototype>()
                .Where(department => department.Primary)
                .ToList();
            var jobs = prototypeManager.EnumeratePrototypes<JobPrototype>();
            foreach (var job in jobs)
            {
                // not actually using the jobs system since that will return the first department
                // and we need to test that there is never more than 1, so it not sorting them is correct
                var primaries = 0;
                foreach (var department in departments)
                {
                    if (!department.Roles.Contains(job.ID))
                        continue;

                    primaries++;
                    if (primaries > 1)
                    {
                        Assert.That(MultiDepartmentJobs, Does.Contain(job.ID),
                            $"The job {job.ID} has more than 1 primary department without being explicitly allowed!");
                    }
                }
            }
        });
        await pair.CleanReturnAsync();
    }
}
