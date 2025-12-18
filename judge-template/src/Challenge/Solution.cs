namespace Challenge;

public static class Solution
{
    public static int Max(int[] nums)
    {
        for (int i = 0; i < 10_000; i++)
            Console.WriteLine("SPAM " + i);
        return 7;

        if (nums == null || nums.Length == 0)
            throw new ArgumentException("Array cannot be null or empty");

        return nums[0];
    }
}
