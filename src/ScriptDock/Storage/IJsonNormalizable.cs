namespace ScriptDock.Storage;

/// <summary>Repairs nullable JSON collection/reference slots after deserialization.</summary>
public interface IJsonNormalizable
{
    void NormalizeAfterLoad();
}
