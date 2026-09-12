namespace InfinityHairartsAPI.Services.Interfaces
{
    public interface IAdmin
    {
        string InsertNewHairCutTpyes(string HairCutItemName, string HairCutItemPrice,IFormFile imgHairCutphoto);
        Tuple<string, List<Dictionary<string, object>>> getHairCutItemDetails(Guid ItemID);
        string UpdateNewHairCutTpyes(Guid UniqueItemID, string HairCutItemName, string HairCutItemPrice, IFormFile imgprofilephoto);
        Tuple<string> deleteHairCutItemDetails(Guid UniqueItemID, string ImageName);
    }
}
