namespace Challenge;

public static class Solution
{
    public static int Max(int[] nums)
    {
        if (nums == null || nums.Length == 0)
            throw new ArgumentException("Array cannot be null or empty");

        return nums[0];
    }
}
