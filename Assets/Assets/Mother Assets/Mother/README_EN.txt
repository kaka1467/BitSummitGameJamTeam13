HAHAOYA - ALL ANIMATIONS ON ONE MASTER RIG

Tested: Unity 6.3 LTS (6000.3.25f1), Universal Render Pipeline.
One mother model, one 65-bone TARGET_MASTER skeleton, 10 Generic clips.
All animation is baked onto the master. No copied Humanoid Avatar is needed.
Original animation timing is preserved. The Blender master uses 120 fps to
represent the original 24/30 fps timing exactly; Unity plays in seconds.

IMPORT AND FIRST TEST
1. Import Hahaoya_All_Master_20261008.unitypackage using:
   Assets > Import Package > Custom Package. Import all items.
2. Open Assets/Hahaoya_All_Master_20261008.
3. Drag Hahaoya_All_Master.prefab into an empty test scene Hierarchy.
4. Keep root Scale (1,1,1), Rotation (0,0,0). Set Position as required.
   ALL THREE SCALE VALUES must be 1. Z=0 flattens the character.
5. In Scene view, select the character and press F to frame it.
6. Press Play. Standing plays automatically.
7. Expand the prefab and select Character (the child with the Animator).
8. Open Window > Animation > Animator. In Parameters, change the integer
   parameter named Clip to select one of the following animations:

Clip 0: Standing       8.167 seconds (loop enabled)
Clip 1: Walk           8.083 seconds (loop enabled)
Clip 2: Peek_Door      6.292 seconds
Clip 3: Peek_Window    8.333 seconds
Clip 4: Jumpscare_01   3.125 seconds
Clip 5: Jumpscare_02   2.667 seconds
Clip 6: Jumpscare_03   3.375 seconds
Clip 7: Gosogoso_01    5.667 seconds
Clip 8: Gosogoso_02    1.233 seconds
Clip 9: Gosogoso_03    5.067 seconds

One-shot clips hold the final pose. To replay the same clip, select another
Clip value and then return, or use Animator.Play with normalizedTime=0.
Changes made while playing are temporary.
Standing/Walk loop seams should be reviewed in the game; the package does
not redesign the authored poses at the start/end of those animations.

PROGRAMMER INTEGRATION
Use the Animator on the Character CHILD of the prefab, not the wrapper root.
Use its included Generic Avatar and Hahaoya_All_Master controller.
Set the Clip integer parameter to the value above from your game code.
Keep Apply Root Motion OFF for the provided test setup.
Keep the existing Character offset: it places the model on the ground.
The skeleton/object transforms and animation paths must stay intact.
Do not rename/reparent TARGET_MASTER or its bones.
The authored movement within each clip is preserved. Check world placement,
clip transitions, collision/navigation, IK, and door/window contact separately.
Existing movement scripts may need adjustment for Generic animation.
Humanoid-only APIs/retargeting cannot be assumed to work with this Generic rig.
Do not assign the old normal rig, hahaoyaAvatar, or previous Humanoid Avatar.
Do not combine these clips with the old normal-rig controller.

PACKAGE CONTENTS AND PROJECT SAFETY
Everything is inside Assets/Hahaoya_All_Master_20261008.
The package contains only the FBX/model/clips, controller, prefab, materials,
and this guide. It contains no scenes, scripts, cameras, lights, or settings.
It adds a separate folder; importing the same package again updates that folder.
It does not automatically replace existing game characters/controllers.
Materials target URP. For Built-in/HDRP, assign pipeline-compatible materials.
Pink material means a shader mismatch; it is separate from animation validity.
Doors, windows, backgrounds, camera animation, and gameplay are not included.

ANIMATION NOTES
Standing and Gosogoso were retargeted from a different rest pose onto the
master bone lengths. Original source files and original Actions were preserved.
Door Peek and all Jumpscares remain on the verified master skeleton.
Review all 10 clips, especially hands and Gosogoso_03 neck shape, in your game.
Window Peek preserves its authored bone motion, including neck movement.

If any test fails, share the Unity version, render pipeline, Console error
text, selected Clip number, and a video of this included prefab test.
