namespace InfinityHairartsAPI.Modals
{
    public class AppoinmentModal
    {
        public class insertupdateSeatBookingDetails
        {
            public int SeatCount { get; set; }
            public string BookingDate { get; set; }
        }
        public class insertCustomerTimeSelection
        {
            public string TimeAllocationID { get; set; }
        }
        public class HairCutItemGroupMasterModal
        {
            public Guid HairCutItemGroupMaster_ID { get; set; }
        }
        public class AddtoCartModal
        {
            public Guid HairCut_Item_ID { get; set; }
            public int seatCount { get; set; }
        }
    }
}
