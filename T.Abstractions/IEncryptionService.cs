namespace T.Abstractions;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string encryptedText);
    bool IsEncrypted(string text);
}