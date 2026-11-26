/// <summary>
/// Optional: lets an interactable choose the words in the "[E] ..." pop-up.
/// Without it the pop-up says "[E] Interact".
/// </summary>
public interface IInteractPrompt
{
	string PromptText { get; }
}
