namespace Challenge.Tests;

[TestFixture]
public class SolutionUnitTests
{
    [Test]
    public void Max_ReturnsMaximumElement()
    {
        int[] nums = { 1, 2, 5, 7, 3 };
        int result = Solution.Max(nums);
        Assert.That(result, Is.EqualTo(7));
    }
}
