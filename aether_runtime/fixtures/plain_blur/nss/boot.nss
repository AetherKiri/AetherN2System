chapter main
{
    CreateTexture("pattern", 1, 0, 0, "pattern.png");
    WaitKey();
    CreateEffect("plain", 2, 0, 0, 320, 180, "Plain");
    Request("plain", Passive);
    SetShade("plain", SEMIHEAVY);
    Delete("pattern");
    WaitKey();
    SetShade("plain", null);
    WaitKey();
    Delete("plain");
    CreateClipTexture("sprite", 2, 0, 0, 80, 30, 160, 120, "pattern.png");
    WaitKey();
    SetShade("sprite", MEDIUM);
    WaitKey();
    SetShade("sprite", null);
    WaitKey();
    SetShade("sprite", NOMORE);
    WaitKey();
    SetShade("sprite", NORMAL);
    WaitKey();
    SetShade("sprite", null);
    WaitKey();
    Delete("sprite");
    Exit();
}
