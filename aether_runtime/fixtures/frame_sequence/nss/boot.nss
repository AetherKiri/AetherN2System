chapter main
{
    CreateColor("background", 0, 0, 0, 320, 180, "BLACK");
    CreateColor("marker", 1, 160, 0, 160, 180, "RED");
    Array($frames, "frames/red.png", "frames/blue.png", "frames/missing.png", "frames/green.png");
    CreateFrameAnimation("anim8", 208, 0, 0, $frames, 2, "frames/yellow.png", false, false, false);
    WaitFrameAnimation("anim8");
    CreateColor("marker", 1, 160, 0, 160, 180, "GREEN");
    WaitKey();
    CreateColor("marker", 1, 160, 0, 160, 180, "RED");
    CreateFrameAnimation("anim8", 208, 0, 0, $frames, 1, "frames/green.png", false, true, false);
    WaitFrameAnimation("anim8");
    CreateColor("marker", 1, 160, 0, 160, 180, "GREEN");
    WaitKey();
    CreateColor("marker", 1, 160, 0, 160, 180, "RED");
    Array($frames, "frames/red.png", "frames/blue.png");
    CreateFrameAnimation("anim8", 208, 0, 0, $frames, 1, "", true, false, true);
    CreateColor("marker", 1, 160, 0, 160, 180, "GREEN");
    WaitKey();
    Exit();
}
