using InputAtlas.App.Pet;
using InputAtlas.Core;

namespace InputAtlas.App.Tests;

public sealed class PetInputMapperTests
{
    [Theory]
    [InlineData(0x04)] // A
    [InlineData(0x0A)] // G
    [InlineData(0x14)] // Q
    [InlineData(0x1D)] // Z
    [InlineData(0xE1)] // left Shift
    public void LeftHandKeysMapToLeftAction(ushort usage)
    {
        Assert.Equal(PetInputAction.KeyboardLeft, PetInputMapper.Map(new InputId(usage)));
    }

    [Theory]
    [InlineData(0x0B)] // H
    [InlineData(0x13)] // P
    [InlineData(0x1C)] // Y
    [InlineData(0x28)] // Enter
    [InlineData(0x59)] // numpad 1
    [InlineData(0xE5)] // right Shift
    public void RightHandKeysMapToRightAction(ushort usage)
    {
        Assert.Equal(PetInputAction.KeyboardRight, PetInputMapper.Map(new InputId(usage)));
    }

    [Fact]
    public void SpaceAndUnknownKeyboardKeysUseStableBothHandsFallback()
    {
        Assert.Equal(PetInputAction.KeyboardBoth, PetInputMapper.Map(new InputId(0x2C)));
        Assert.Equal(PetInputAction.KeyboardBoth, PetInputMapper.Map(InputId.OtherKeyboard));
        Assert.Equal(PetInputAction.KeyboardBoth, PetInputMapper.Map(new InputId(0x90)));
    }

    [Fact]
    public void MouseButtonsMapButWheelAndOtherMouseAreRejected()
    {
        Assert.Equal(PetInputAction.MouseLeft, PetInputMapper.Map(InputId.MouseLeft));
        Assert.Equal(PetInputAction.MouseRight, PetInputMapper.Map(InputId.MouseRight));
        Assert.Equal(PetInputAction.MouseMiddle, PetInputMapper.Map(InputId.MouseMiddle));
        Assert.Equal(PetInputAction.MouseBack, PetInputMapper.Map(InputId.MouseBack));
        Assert.Equal(PetInputAction.MouseForward, PetInputMapper.Map(InputId.MouseForward));
        Assert.Equal(PetInputAction.None, PetInputMapper.Map(InputId.WheelUp));
        Assert.Equal(PetInputAction.None, PetInputMapper.Map(InputId.WheelDown));
        Assert.Equal(PetInputAction.None, PetInputMapper.Map(InputId.WheelLeft));
        Assert.Equal(PetInputAction.None, PetInputMapper.Map(InputId.WheelRight));
        Assert.Equal(PetInputAction.None, PetInputMapper.Map(InputId.OtherMouse));
    }
}
